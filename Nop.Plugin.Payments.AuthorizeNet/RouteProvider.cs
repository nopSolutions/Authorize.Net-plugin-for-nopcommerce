using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Nop.Web.Framework.Mvc.Routing;

namespace Nop.Plugin.Payments.AuthorizeNet
{
    public partial class RouteProvider : IRouteProvider
    {
        public void RegisterRoutes(IRouteBuilder routeBuilder)
        {
            //IPN
            routeBuilder.MapRoute("Plugin.Payments.AuthorizeNet.IPNHandler", "Plugins/PaymentAuthorizeNet/IPNHandler",
                new { controller = "PaymentAuthorizeNet", action = "IPNHandler" });
        }

        public int Priority => 0;
    }
}
