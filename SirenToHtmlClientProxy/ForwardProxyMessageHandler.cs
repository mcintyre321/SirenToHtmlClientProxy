using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Xml.Linq;
using CsQuery;
using FormFactory;
using FormFactory.Attributes;
using FormFactory.RazorEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OneOf;
using RazorEngine.Text;
using SirenDotNet;
using Action = SirenDotNet.Action;
using Exception = System.Exception;

namespace SirenToHtmlClientProxy
{
    public class ForwardProxyMessageHandler : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var userIp = HttpContext.Current.Request.UserHostAddress;
            request.Headers.Add("X-Forwarded-For", userIp);

            IEnumerable<string> destinations;
            if (!request.Headers.TryGetValues("X-Destination", out destinations))
            {
                return request.CreateErrorResponse(HttpStatusCode.BadRequest,
                    "Please add an X-Destination header e.g. http://firstproxy, http://secondproxy http://endwarehost");
            }

            if (request.Method == HttpMethod.Get || request.Method == HttpMethod.Trace) request.Content = null;

            var first = new Uri(destinations.First());

            Func<string, string> sanitiseUrls = s => s.Replace(first.ToString(), "/");

            var uri = new UriBuilder(first.ToString().TrimEnd('/') + '/' + request.RequestUri.PathAndQuery.TrimStart('/'));

            uri.Host = first.Host;
            uri.Port = first.Port;
            request.Headers.Clear();
            request.Headers.Remove("X-Destination");
            if (destinations.Skip(1).Any())
                request.Headers.Add("X-Destination", destinations.Skip(1));
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.siren+json"));

            request.Headers.Host = uri.Host;
            request.Headers.AcceptEncoding.Clear();

            var qs = request.RequestUri.ParseQueryString();

            var methodOverride = qs["_method"];


            if (methodOverride != null)
            {
                request.Method = new HttpMethod(methodOverride);
                qs.Remove("_method");
            }

            uri.Query = qs.ToString();
            request.RequestUri = new Uri(uri.ToString());
            if (request.Content != null) await request.Content.LoadIntoBufferAsync();
 
            List<string> httpLog = new List<string>();

            using (var httpClient = new HttpClient())
            {

                var requestInitialContent = request.Content;
                if (requestInitialContent != null)
                {
                    await requestInitialContent.LoadIntoBufferAsync();
                    var requestContentStream = await requestInitialContent.ReadAsStreamAsync();
                    request.Content = new StreamContent(requestContentStream);
                    requestInitialContent.Headers.ToList()
                        .ForEach(kvp => request.Content.Headers.TryAddWithoutValidation(kvp.Key, kvp.Value));
                }

                var responseMessage = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                request.Content = requestInitialContent;
                
                httpLog.Add(ToString(request));
                httpLog.Add(ToString(responseMessage));
                responseMessage.Headers.TransferEncodingChunked = null; //throws an error on calls to WebApi results
                if (responseMessage.StatusCode == HttpStatusCode.NotModified) return responseMessage;
                if (request.Method == HttpMethod.Head) responseMessage.Content = null;
                if (responseMessage.Content != null)
                {
                    try
                    {
                        if (request.Method == HttpMethod.Post)
                        {
                            if (responseMessage.StatusCode == HttpStatusCode.Created
                                || responseMessage.StatusCode == HttpStatusCode.Accepted)
                            {
                                if (responseMessage.Headers.Location != null)
                                {
                                    var redirectMessage = request.CreateResponse(HttpStatusCode.Moved);
                                    redirectMessage.Headers.Location =
                                        new Uri(sanitiseUrls(responseMessage.Headers.Location.ToString()),
                                            UriKind.Relative);
                                    return redirectMessage;
                                }
                            }
                            if (responseMessage.StatusCode == HttpStatusCode.OK)
                            {
                                //fall through to get handler
                            }
                            else
                            {
                                throw new NotImplementedException();
                            }
                        }
                        if (responseMessage.Content?.Headers?.ContentType?.MediaType == "application/vnd.siren+json")
                        {
                            var content = await responseMessage.Content.ReadAsStringAsync();

                            var html = ReadSirenAndConvertToForm(sanitiseUrls(content));
                            html = SplitContainer(html, string.Join(Environment.NewLine,httpLog));
                             
                            var stringContent = new StringContent(html, Encoding.Default, "text/html");
                            responseMessage.Content = stringContent;
                        }
                    }
                    catch (Exception ex)
                    {
                        responseMessage.Content =
                            new StringContent(request.RequestUri.ToString() + request.Headers.ToString() + ex.ToString());
                    }

                }
                return responseMessage;
            }
        }

   

        private static string ReadSirenAndConvertToForm(string content)
        {
            var jo = JObject.Parse(content);
            var entity = jo.ToObject<SirenDotNet.Entity>();
            return BuildComponentsForEntity(entity, 1);
        }

        private static string BuildComponentsForEntity(Entity entity, int depth)
        {

            
            var list = new List<string>();

            if (entity.Title != null)
            {
                list.Add($"<h{depth}>{entity.Title}</h{depth}>");
            }

            list.AddRange(entity.Links?.Select(x => BuildPropertyVmFromLink(x).Render(new RazorTemplateHtmlHelper()).ToString()) ?? new List<string>());
           
            list.AddRange(entity.Entities?.Select(e => BuildPropertyVmFromSubEntity(e, depth++))
                .Select(entityPropertiesHtml => string.Join(Environment.NewLine, entityPropertiesHtml))
                .Select(entityHtml => CsQuery.CQ.Create("<div>").Html(entityHtml).AddClass("subentity").AddClass("depth" + depth).Render())
                .ToList() ?? new List<string>())
                
            ;
            ;
            entity.Properties?.Properties().Select(PropertyVmFromJToken).ToList().ForEach(p => list.Add(p.Render(new RazorTemplateHtmlHelper()).ToString()));
            entity.Actions?.Select(BuildFormVmFromAction).ToList().ForEach(x => list.Add(x.Render(new RazorTemplateHtmlHelper()).ToString()));
            return CQ.Create("<div>").Html(string.Join(Environment.NewLine, list)).AddClass("entity").AddClass("depth" + depth).Render();

        }

        private static IEnumerable<string> BuildPropertyVmFromSubEntity(SubEntity e, int depth)
        {
            var list = new List<string>();

            var embedded = e as SubEntity.Embedded;
            if (embedded != null)
            {
                if (embedded.Title != null)
                {
                    var propertyVm = new PropertyVm(typeof(XElement), "title")
                    {
                        Value = new XElement("h" + depth, embedded.Title),
                        Readonly = true,
                        DisplayName = "Title"
                    };

                    list.Add(propertyVm.Render(new RazorTemplateHtmlHelper()).ToString());
                }

                embedded.Links?.Select(BuildPropertyVmFromLink).ToList().ForEach(x => list.Add(x.Render(new RazorTemplateHtmlHelper()).ToString()));
                embedded.Properties?.Properties()
                    .Select(PropertyVmFromJToken)
                    .ToList()
                    .ForEach(x => list.Add(x.Render(new RazorTemplateHtmlHelper()).ToString()));
                embedded.Actions?.Select(BuildFormVmFromAction).ToList().ForEach(x => list.Add(x.Render(new RazorTemplateHtmlHelper()).ToString()));


                list.AddRange(embedded.Entities?.Select(e1 => BuildPropertyVmFromSubEntity(e1, depth++))
                    .Select(entityPropertiesHtml => string.Join(Environment.NewLine, entityPropertiesHtml))
                    .Select(
                        entityHtml =>
                            CsQuery.CQ.Create("<div>")
                                .Html(entityHtml)
                                .AddClass("entity")
                                .AddClass("depth" + depth)
                                .Render())
                    .ToList() ?? new List<string>());

                return list.AsEnumerable();
            }
            var linked = (SubEntity.Linked) e;
            if (linked != null)
            {
                return new[]
                {
                    $"<a href='{linked.Href}'>{linked.Title ?? linked.Href.ToString()}</a>"
                };
            }
            //not implemented
            return list;
        }

        private static PropertyVm PropertyVmFromJToken(JProperty property)
        {
            var propertyVm = new PropertyVm(typeof(string), property.Name)
            {
                Value = property.Value.ToString(),
                Readonly = true,
                DisplayName = property.Name,

            };
            //propertyVm.GetCustomAttributes = () => new object[] {new DataTypeAttribute(DataType.MultilineText)};
            return propertyVm;
        }

        private static PropertyVm BuildPropertyVmFromLink(Link link)
        {
            var element = new XElement("a", new XAttribute("href", link.Href));

            var rels = string.Join(", ", link.Rel);
            element.SetAttributeValue("title", rels);
            element.Value = link.Title ?? rels;
            var propertyVm = new PropertyVm(typeof(XElement), rels)
            {
                Value = element,
                Readonly = true,
                DisplayName = rels,
                Name = rels,
                GetCustomAttributes = () => new object[] {new NoLabelAttribute()}
            };
            return propertyVm;
        }

        private static string ToString(HttpRequestMessage req)
        {
            return CsQuery.CQ.Create("<pre>").Text($"{req.Method} {req.RequestUri}\r\n" +
                   req.Headers.ToString() +
                   req.Content?.Headers.ToString() +
                   FormatIfJson(req.Content)).AddClass("request").Render();
        }
        private string ToString(HttpResponseMessage resp)
        {
            return CsQuery.CQ.Create("<pre>").Text($"{(int)resp.StatusCode} {resp.StatusCode}\r\n" +
                   resp.Headers.ToString() +
                   resp.Content?.Headers.ToString() +
                   FormatIfJson(resp.Content)).AddClass("response").AddClass("status" + (int)resp.StatusCode).Render();
        }
        private static string FormatIfJson(HttpContent content)
        {
            var mediaType = content?.Headers?.ContentType?.MediaType;
            if (mediaType == "application/json" || mediaType == "application/vnd.siren+json")
            {
                return JToken.Parse(content.ReadAsStringAsync().Result).ToString(Formatting.Indented);
            }
            return content?.ReadAsStringAsync().Result;
        }

        private static FormVm BuildFormVmFromAction(Action action)
        {
            var form = new FormVm
            {
                ActionUrl = action.Href.ToString(),
                DisplayName = action.Title ?? action.Name ?? "link",
                Method = action?.Method.ToString().ToLower(),
                Inputs =
                    action.Fields?.Select(field => new PropertyVm(typeof(string), field.Name) {DisplayName = field.Name})
                        ?.ToArray() ?? Enumerable.Empty<PropertyVm>(),

            };

            if (form.Method != "get" && form.Method != "post")
            {
                form.ActionUrl += form.ActionUrl.Contains("?") ? "&" : "?";
                form.ActionUrl += "_method=" + form.Method.ToString().ToUpper();
                form.Method = "post";
            }
            return form;
        }

        string SplitContainer(string left, string right)
        {
            return
                $@"
<div class=""split-container"">
  <div class=""split-item split-left"">
     {left}
  </div>
  <div class=""split-item split-right"">
     {right}
  </div>
</div>
<style>

.split-container {{
  -webkit-box-orient: horizontal;
  -webkit-box-direction: normal;
  -webkit-flex-direction: row;
  -ms-flex-direction: row;
  flex-direction: row;
  display: -webkit-box;
  display: -webkit-flex;
  display: -ms-flexbox;
  display: flex;
}}

.split-item {{
  
  
  display: -webkit-box;
  display: -webkit-flex;
  display: -ms-flexbox;
  display: flex;
  -webkit-box-orient: vertical;
  -webkit-box-direction: normal;
  -webkit-flex-direction: column;
  -ms-flex-direction: column;
  flex-direction: column;
  
  
  width: 50%;
  padding: 3em 5em 6em 5em;
}}
 
.request{{
  background-color: aliceblue;  
}}

.response{{
  background-color: lightgrey;
}}

.response.status200{{
  background-color: f5fff0
}}

.entity, .subentity {{
    background-color: rgba(220, 220, 220, 0.1);
    border: solid 1px rgba(220, 220, 220, 0.2);
    padding: 10px 10px;
    -webkit-border-radius: 5px;
    -moz-border-radius: 5px;
    border-radius: 5px;
}}

</style>
";
        }

       
    }

}
