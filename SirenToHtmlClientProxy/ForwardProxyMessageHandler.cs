using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Xml.Linq;
using FormFactory;
using FormFactory.RazorEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OneOf;
using RazorEngine.Text;
using SirenSharp;
using Action = SirenSharp.Action;
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
                    "Please add an X-Destination header e.g. firstproxy:80, secondproxy:8080 endwarehost:80");
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

            using (var httpClient = new HttpClient())
            {
                var responseMessage =
                    await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

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
                        var content = await responseMessage.Content.ReadAsStringAsync();
                        var htmls = ReadSirenAndConvertToForm(sanitiseUrls(content));
                        var stringContent = new StringContent(htmls, Encoding.Default, "text/html");
                        responseMessage.Content = stringContent;
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
            var entity = JsonConvert.DeserializeObject<SirenSharp.Entity>(content);
            var list = new List<OneOf<PropertyVm, FormVm>>();

            entity.Links?.Select(BuildPropertyVmFromLink).ToList().ForEach(x => list.Add(x));
            entity.Properties?.Select(PropertyVmFromJToken).ToList().ForEach(x => list.Add(x));
            entity.Actions?.Select(BuildFormVmFromAction).ToList().ForEach(x => list.Add(x));
            entity.Entities?.Select(BuildPropertyVmFromSubEntity).ToList().ForEach(x => list.AddRange(x));

            var razorHelper = new FormFactory.RazorEngine.RazorTemplateHtmlHelper();
            var elements = list
                .Select(x => x.Match(
                    propertyVm => propertyVm.Render(razorHelper),
                    formVm => ((FormVm) formVm).Render(razorHelper)
                    )
                ).ToList();


            var sirenElement = new PropertyVm(typeof(string), "_response")
            {
                Readonly = true,
                DisplayName = "Siren Response",
                GetCustomAttributes =  () => new object[] { new DataTypeAttribute(DataType.MultilineText) },
                Value = JToken.Parse(content).ToString(Formatting.Indented)
            };

            elements.Add(sirenElement.Render(razorHelper));

            return string.Join("", elements.Select(htmlStr => htmlStr.ToString()));
        }

        private static IEnumerable<OneOf<PropertyVm, FormVm>> BuildPropertyVmFromSubEntity(
            OneOf<SubEntity, SubEntityLink> e)
        {
            return e.Match(
                subEntity =>
                {
                    var list = new List<OneOf<PropertyVm, FormVm>>();
                    subEntity.Links?.Select(BuildPropertyVmFromLink).ToList().ForEach(x => list.Add(x));
                    subEntity.Properties?.Select(PropertyVmFromJToken).ToList().ForEach(x => list.Add(x));
                    subEntity.Actions?.Select(BuildFormVmFromAction).ToList().ForEach(x => list.Add(x));
                    subEntity.Entities?.Select(BuildPropertyVmFromSubEntity).ToList().ForEach(x => list.AddRange(x));
                    return list.AsEnumerable();
                },
                subEntity => Enumerable.Empty<OneOf<PropertyVm, FormVm>>().AsEnumerable()
            );
        }

        private static PropertyVm PropertyVmFromJToken(KeyValuePair<string, JToken> property)
        {
            var propertyVm = new PropertyVm(typeof(string), property.Key)
            {
                Value = property.Value.ToString(),
                Readonly = true,
                DisplayName = property.Key
            };
            propertyVm.GetCustomAttributes = () => new object[] {new DataTypeAttribute(DataType.MultilineText)};
            return propertyVm;
        }

        private static PropertyVm BuildPropertyVmFromLink(Link link)
        {
            var element = new XElement("a", new XAttribute("href", link.Href));
            var name = string.Join(", ", link.Rel);

            element.Value = name;
            var propertyVm = new PropertyVm(typeof(XElement), name)
            {
                Value = element,
                Readonly = true,
                DisplayName = name,
                Name = name
            };
            return propertyVm;
        }

        private static FormVm BuildFormVmFromAction(Action action)
        {
            var form = new FormVm
            {
                ActionUrl = action.Href.ToString(),
                DisplayName = action.Title ?? action.Name ?? "link",
                Method = action?.Method.ToString().ToLower(),
                Inputs = action.Fields?.Select(field => new PropertyVm(typeof(string), field.Name) {DisplayName = field.Name})?.ToArray() ?? Enumerable.Empty<PropertyVm>()
            };

            if (form.Method != "get" && form.Method != "post")
            {
                form.ActionUrl += form.ActionUrl.Contains("?") ? "&" : "?";
                form.ActionUrl += "_method=" + form.Method.ToString().ToUpper();
                form.Method = "post";
            }
            return form;
        }
    }
}