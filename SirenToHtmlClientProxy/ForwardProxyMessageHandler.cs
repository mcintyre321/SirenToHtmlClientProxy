using System;
using System.Collections.Generic;
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
using OneOf;

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
            var uri = new UriBuilder(first);

            uri.Host = first.Host;
            uri.Port = first.Port;
            request.Headers.Clear();
            request.Headers.Remove("X-Destination");
            if (destinations.Skip(1).Any())
                request.Headers.Add("X-Destination", destinations.Skip(1));
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.siren+json"));

            request.Headers.Host = uri.Host;
            request.RequestUri = new Uri(uri.ToString());
            request.Headers.AcceptEncoding.Clear();
 
            var responseMessage =
                await
                    new HttpClient().SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken);

            responseMessage.Headers.TransferEncodingChunked = null; //throws an error on calls to WebApi results
            if (responseMessage.StatusCode == HttpStatusCode.NotModified) return responseMessage;
            if (request.Method == HttpMethod.Head) responseMessage.Content = null;
            if (responseMessage.Content != null)
            {
                try
                {
                    var content = await responseMessage.Content.ReadAsStringAsync();
                    content = content.Replace(first.ToString(), "/");
                    var sirenObject = JsonConvert.DeserializeObject<SirenSharp.Entity>(content);
                    var htmlModel = new List<OneOf<PropertyVm, FormVm>>();
                    foreach (var property in sirenObject.Properties)
                    {
                        htmlModel.Add(new PropertyVm(typeof(string), property.Key)
                        {
                            Value = property.Value.ToString(),
                            Readonly = true,
                            DisplayName = property.Key
                        });
                    }
                    foreach (var link in sirenObject.Links)
                    {
                        var element = new XElement("a", new XAttribute("href", link.Href));
                        var name = string.Join(", ", link.Rel);
                        htmlModel.Add(new PropertyVm(typeof(XElement), name)
                        {
                            Value = element,
                            Readonly = true,
                            DisplayName = name
                        });
                    }
                    foreach (var action in sirenObject.Actions)
                    {
                        var form = new FormVm
                        {
                            ActionUrl = action.Href.ToString(),
                            DisplayName = action.Title ?? action.Name,
                            Method = action.Method.ToString(),
                            Inputs = action.Fields.Select(field =>
                            {
                                var property = new PropertyVm(typeof(string), field.Name) {DisplayName = field.Name};
                                return property;
                            })
                        };
                        htmlModel.Add(form);
                    }
                     
                    var razorHelper = new FormFactory.RazorEngine.RazorTemplateHtmlHelper();
                    var htmls = htmlModel
                        .Select(x => x.Match(
                            propertyVm => propertyVm.Render(razorHelper),
                            formVm => formVm.Render(razorHelper)
                            )
                        )
                        .Select(htmlStr => htmlStr.ToString());
                    responseMessage.Content = new StringContent(string.Join("", htmls), Encoding.Default, "text/html");
                }
                catch (Exception ex)
                {
                    responseMessage.Content = new StringContent(request.RequestUri.ToString() + request.Headers.ToString()+ ex.ToString());
                }
            }

            return responseMessage;
        }

    }
}