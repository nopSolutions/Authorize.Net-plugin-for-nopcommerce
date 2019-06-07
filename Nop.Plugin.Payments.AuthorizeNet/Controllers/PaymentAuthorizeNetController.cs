using System;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Nop.Core;
using Nop.Plugin.Payments.AuthorizeNet.Models;
using Nop.Services;
using Nop.Services.Configuration;
using Nop.Services.Localization;
using Nop.Services.Messages;
using Nop.Services.Payments;
using Nop.Services.Security;
using Nop.Web.Framework;
using Nop.Web.Framework.Controllers;
using Nop.Web.Framework.Mvc.Filters;

namespace Nop.Plugin.Payments.AuthorizeNet.Controllers
{
    public class PaymentAuthorizeNetController : BasePaymentController
    {
        private readonly ILocalizationService _localizationService;
        private readonly INotificationService _notificationService;
        private readonly ISettingService _settingService;
        private readonly IStoreContext _storeContext;
        private readonly IPaymentPluginManager _paymentPluginManager;
        private readonly IPermissionService _permissionService;

        public PaymentAuthorizeNetController(ILocalizationService localizationService,
            INotificationService notificationService,
            ISettingService settingService,
            IStoreContext storeContext,
            IPaymentPluginManager paymentPluginManager,
            IPaymentService paymentService,
            IPermissionService permissionService)
        {
            _localizationService = localizationService;
            _notificationService = notificationService;
            _settingService = settingService;
            _storeContext = storeContext;
            _paymentPluginManager = paymentPluginManager;
            _permissionService = permissionService;
        }

        [AuthorizeAdmin]
        [Area(AreaNames.Admin)]
        public IActionResult Configure()
        {
            if (!_permissionService.Authorize(StandardPermissionProvider.ManagePaymentMethods))
                return AccessDeniedView();

            //load settings for a chosen store scope
            var storeScope = _storeContext.ActiveStoreScopeConfiguration;
            var authorizeNetPaymentSettings = _settingService.LoadSetting<AuthorizeNetPaymentSettings>(storeScope);

            var model = new ConfigurationModel
            {
                UseSandbox = authorizeNetPaymentSettings.UseSandbox,
                UseShippingAddressAsBilling = authorizeNetPaymentSettings.UseShippingAddressAsBilling,
                TransactModeId = Convert.ToInt32(authorizeNetPaymentSettings.TransactMode),
                TransactionKey = authorizeNetPaymentSettings.TransactionKey,
                LoginId = authorizeNetPaymentSettings.LoginId,
                AdditionalFee = authorizeNetPaymentSettings.AdditionalFee,
                AdditionalFeePercentage = authorizeNetPaymentSettings.AdditionalFeePercentage,
                TransactModeValues = authorizeNetPaymentSettings.TransactMode.ToSelectList(),
                ActiveStoreScopeConfiguration = storeScope
            };

            if (storeScope > 0)
            {
                model.UseSandbox_OverrideForStore = _settingService.SettingExists(authorizeNetPaymentSettings, x => x.UseSandbox, storeScope);
                model.UseShippingAddressAsBilling_OverrideForStore = _settingService.SettingExists(authorizeNetPaymentSettings, x => x.UseShippingAddressAsBilling, storeScope);
                model.TransactModeId_OverrideForStore = _settingService.SettingExists(authorizeNetPaymentSettings, x => x.TransactMode, storeScope);
                model.TransactionKey_OverrideForStore = _settingService.SettingExists(authorizeNetPaymentSettings, x => x.TransactionKey, storeScope);
                model.LoginId_OverrideForStore = _settingService.SettingExists(authorizeNetPaymentSettings, x => x.LoginId, storeScope);
                model.AdditionalFee_OverrideForStore = _settingService.SettingExists(authorizeNetPaymentSettings, x => x.AdditionalFee, storeScope);
                model.AdditionalFeePercentage_OverrideForStore = _settingService.SettingExists(authorizeNetPaymentSettings, x => x.AdditionalFeePercentage, storeScope);
            }

            return View("~/Plugins/Payments.AuthorizeNet/Views/Configure.cshtml", model);
        }

        [HttpPost]
        [AuthorizeAdmin]
        [Area(AreaNames.Admin)]
        public IActionResult Configure(ConfigurationModel model)
        {
            if (!_permissionService.Authorize(StandardPermissionProvider.ManagePaymentMethods))
                return AccessDeniedView();

            if (!ModelState.IsValid)
                return Configure();

            //load settings for a chosen store scope
            var storeScope = _storeContext.ActiveStoreScopeConfiguration;
            var authorizeNetPaymentSettings = _settingService.LoadSetting<AuthorizeNetPaymentSettings>(storeScope);

            //save settings
            authorizeNetPaymentSettings.UseSandbox = model.UseSandbox;
            authorizeNetPaymentSettings.UseShippingAddressAsBilling = model.UseShippingAddressAsBilling;
            authorizeNetPaymentSettings.TransactMode = (TransactMode)model.TransactModeId;
            authorizeNetPaymentSettings.TransactionKey = model.TransactionKey;
            authorizeNetPaymentSettings.LoginId = model.LoginId;
            authorizeNetPaymentSettings.AdditionalFee = model.AdditionalFee;
            authorizeNetPaymentSettings.AdditionalFeePercentage = model.AdditionalFeePercentage;

            /* We do not clear cache after each setting update.
             * This behavior can increase performance because cached settings will not be cleared 
             * and loaded from database after each update */
            _settingService.SaveSettingOverridablePerStore(authorizeNetPaymentSettings, x => x.UseSandbox, model.UseSandbox_OverrideForStore, storeScope, false);
            _settingService.SaveSettingOverridablePerStore(authorizeNetPaymentSettings, x => x.UseShippingAddressAsBilling, model.UseShippingAddressAsBilling_OverrideForStore, storeScope, false);
            _settingService.SaveSettingOverridablePerStore(authorizeNetPaymentSettings, x => x.TransactMode, model.TransactModeId_OverrideForStore, storeScope, false);
            _settingService.SaveSettingOverridablePerStore(authorizeNetPaymentSettings, x => x.TransactionKey, model.TransactionKey_OverrideForStore, storeScope, false);
            _settingService.SaveSettingOverridablePerStore(authorizeNetPaymentSettings, x => x.LoginId, model.LoginId_OverrideForStore, storeScope, false);
            _settingService.SaveSettingOverridablePerStore(authorizeNetPaymentSettings, x => x.AdditionalFee, model.AdditionalFee_OverrideForStore, storeScope, false);
            _settingService.SaveSettingOverridablePerStore(authorizeNetPaymentSettings, x => x.AdditionalFeePercentage, model.AdditionalFeePercentage_OverrideForStore, storeScope, false);

            //now clear settings cache
            _settingService.ClearCache();

            _notificationService.SuccessNotification(_localizationService.GetResource("Admin.Plugins.Saved"));

            return Configure();
        }

        public IActionResult IPNHandler(IpnModel model, IFormCollection form)
        {
            if (!(_paymentPluginManager.LoadPluginBySystemName("Payments.AuthorizeNet") is AuthorizeNetPaymentProcessor processor) || !_paymentPluginManager.IsPluginActive(processor) ||
                !processor.PluginDescriptor.Installed)
                throw new NopException("AuthorizeNet module cannot be loaded");

            var responseCode = form.Keys.Contains("x_response_code") ? form["x_response_code"].ToString() : string.Empty;

            if (responseCode == "1")
            {
                var transactionId = form.Keys.Contains("x_trans_id") ? form["x_trans_id"].ToString() : string.Empty;

                processor.ProcessRecurringPayment(transactionId);
            }

            //nothing should be rendered to visitor
            return Content(string.Empty);
        }
    }
}