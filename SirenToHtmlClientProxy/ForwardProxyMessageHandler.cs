using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using FormFactory;
using FormFactory.RazorEngine;
using Newtonsoft.Json;

namespace SirenToHtmlClientProxy
{
    public class ForwardProxyMessageHandler : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
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

            var uri = new UriBuilder(request.RequestUri);
            uri.Host = destinations.First().Split(':').First();
            uri.Port = int.Parse(destinations.First().Split(':').Skip(1).FirstOrDefault() ?? "80");

            request.Headers.Remove("X-Destination");
            if (destinations.Skip(1).Any())
                request.Headers.Add("X-Destination", destinations.Skip(1));

            request.Headers.Host = uri.Host;
            request.RequestUri = new Uri(uri.ToString());  
            request.Headers.AcceptEncoding.Clear();
            var proxyClientHandler = new HttpClientHandler
            {
                Proxy = new WebProxy("http://localhost:8888", false),
                UseProxy = true
            };
            var responseMessage = await new HttpClient(proxyClientHandler).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            responseMessage.Headers.TransferEncodingChunked = null; //throws an error on calls to WebApi results
            if (request.Method == HttpMethod.Head) responseMessage.Content = null;
            if (responseMessage.Content != null)
            {
                var content = await responseMessage.Content.ReadAsStringAsync();
                var sirenObject = JsonConvert.DeserializeObject<SirenSharp.Entity>(content);
                var htmlModel = new List<PropertyVm>();
                foreach (var property in sirenObject.Properties)
                {
                    htmlModel.Add(new PropertyVm(typeof(string), property.Key)
                    {
                        Value = property.Value.ToString(),
                        Readonly = true,
                        DisplayName = property.Key
                    });
                }

                var razorHelper = new FormFactory.RazorEngine.RazorTemplateHtmlHelper();
                var html = htmlModel.Render(razorHelper).ToString();
                responseMessage.Content = new StringContent(html, Encoding.Default, "text/html");

            }

            return responseMessage;
        }

    }
}