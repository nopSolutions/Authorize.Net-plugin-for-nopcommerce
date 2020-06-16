using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Nop.Web.Framework.Mvc.Routing;

namespace Nop.Plugin.Payments.AuthorizeNet
{
    public partial class RouteProvider : IRouteProvider
    {
        /// <summary>
        /// Register routes
        /// </summary>
        /// <param name="endpointRouteBuilder">Route builder</param>
        public void RegisterRoutes(IEndpointRouteBuilder endpointRouteBuilder)
        {
            //IPN
            endpointRouteBuilder.MapControllerRoute("Plugin.Payments.AuthorizeNet.IPNHandler", "Plugins/AuthorizeNet/IPNHandler",
                new { controller = "AuthorizeNet", action = "IPNHandler" });
        }

        public int Priority => 0;
    }
}
