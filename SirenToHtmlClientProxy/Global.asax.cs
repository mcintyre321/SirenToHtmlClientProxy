using System.Web.Http;
using System.Web.Optimization;
using System.Web.Routing;
using Microsoft.Owin.Security.OAuth;

namespace SirenToHtmlClientProxy
{
    public class WebApiApplication : System.Web.HttpApplication
    {
        protected void Application_Start()
        {
            GlobalConfiguration.Configure(config =>
            {
                config.MessageHandlers.Clear();
                config.MessageHandlers.Add(new ForwardProxyMessageHandler());
                config.IncludeErrorDetailPolicy = IncludeErrorDetailPolicy.Always;
                ;
                config.Routes.MapHttpRoute(
                    name: "DefaultApi",
                    routeTemplate: "{*path}",
                    defaults: new {controller = "Non", id = RouteParameter.Optional}
                    );
            });
        }
    }
}

