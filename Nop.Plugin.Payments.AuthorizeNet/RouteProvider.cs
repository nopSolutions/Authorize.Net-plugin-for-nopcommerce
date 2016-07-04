using System.Web.Mvc;
using System.Web.Routing;
using Nop.Web.Framework.Mvc.Routes;

namespace Nop.Plugin.Payments.AuthorizeNet
{
    public partial class RouteProvider : IRouteProvider
    {
        public void RegisterRoutes(RouteCollection routes)
        {
            //IPN
            routes.MapRoute("Plugin.Payments.AuthorizeNet.IPNHandler",
                 "Plugins/PaymentAuthorizeNet/IPNHandler",
                 new { controller = "PaymentAuthorizeNet", action = "IPNHandler" },
                 new[] { "Nop.Plugin.Payments.AuthorizeNet.Controllers" }
            );
        }
        public int Priority
        {
            get
            {
                return 0;
            }
        }
    }
}
