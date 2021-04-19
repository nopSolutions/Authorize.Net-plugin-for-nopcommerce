using System;
using System.Threading.Tasks;
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
    public class AuthorizeNetController : BasePaymentController
    {
        #region Fields

        private readonly ILocalizationService _localizationService;
        private readonly INotificationService _notificationService;
        private readonly IPaymentPluginManager _paymentPluginManager;
        private readonly IPermissionService _permissionService;
        private readonly ISettingService _settingService;
        private readonly IStoreContext _storeContext;

        #endregion

        #region Ctor

        public AuthorizeNetController(ILocalizationService localizationService,
            INotificationService notificationService,
            IPaymentPluginManager paymentPluginManager,
            IPermissionService permissionService,
            ISettingService settingService,
            IStoreContext storeContext)
        {
            _localizationService = localizationService;
            _notificationService = notificationService;
            _paymentPluginManager = paymentPluginManager;
            _permissionService = permissionService;
            _settingService = settingService;
            _storeContext = storeContext;
        }

        #endregion

        #region Methods

        [AuthorizeAdmin]
        [Area(AreaNames.Admin)]
        [AutoValidateAntiforgeryToken]
        public async Task<IActionResult> Configure()
        {
            if (!await _permissionService.AuthorizeAsync(StandardPermissionProvider.ManagePaymentMethods))
                return AccessDeniedView();

            //load settings for a chosen store scope
            var storeScope = await _storeContext.GetActiveStoreScopeConfigurationAsync();
            var authorizeNetPaymentSettings = await _settingService.LoadSettingAsync<AuthorizeNetPaymentSettings>(storeScope);

            var model = new ConfigurationModel
            {
                UseSandbox = authorizeNetPaymentSettings.UseSandbox,
                UseShippingAddressAsBilling = authorizeNetPaymentSettings.UseShippingAddressAsBilling,
                TransactModeId = Convert.ToInt32(authorizeNetPaymentSettings.TransactMode),
                TransactionKey = authorizeNetPaymentSettings.TransactionKey,
                LoginId = authorizeNetPaymentSettings.LoginId,
                AdditionalFee = authorizeNetPaymentSettings.AdditionalFee,
                AdditionalFeePercentage = authorizeNetPaymentSettings.AdditionalFeePercentage,
                TransactModeValues = await authorizeNetPaymentSettings.TransactMode.ToSelectListAsync(),
                ActiveStoreScopeConfiguration = storeScope
            };

            if (storeScope > 0)
            {
                model.UseSandbox_OverrideForStore = await _settingService.SettingExistsAsync(authorizeNetPaymentSettings, x => x.UseSandbox, storeScope);
                model.UseShippingAddressAsBilling_OverrideForStore = await _settingService.SettingExistsAsync(authorizeNetPaymentSettings, x => x.UseShippingAddressAsBilling, storeScope);
                model.TransactModeId_OverrideForStore = await _settingService.SettingExistsAsync(authorizeNetPaymentSettings, x => x.TransactMode, storeScope);
                model.TransactionKey_OverrideForStore = await _settingService.SettingExistsAsync(authorizeNetPaymentSettings, x => x.TransactionKey, storeScope);
                model.LoginId_OverrideForStore = await _settingService.SettingExistsAsync(authorizeNetPaymentSettings, x => x.LoginId, storeScope);
                model.AdditionalFee_OverrideForStore = await _settingService.SettingExistsAsync(authorizeNetPaymentSettings, x => x.AdditionalFee, storeScope);
                model.AdditionalFeePercentage_OverrideForStore = await _settingService.SettingExistsAsync(authorizeNetPaymentSettings, x => x.AdditionalFeePercentage, storeScope);
            }

            return View("~/Plugins/Payments.AuthorizeNet/Views/Configure.cshtml", model);
        }

        [HttpPost]
        [AuthorizeAdmin]
        [Area(AreaNames.Admin)]
        [AutoValidateAntiforgeryToken]
        public async Task<IActionResult> Configure(ConfigurationModel model)
        {
            if (!await _permissionService.AuthorizeAsync(StandardPermissionProvider.ManagePaymentMethods))
                return AccessDeniedView();

            if (!ModelState.IsValid)
                return await Configure();

            //load settings for a chosen store scope
            var storeScope = await _storeContext.GetActiveStoreScopeConfigurationAsync();
            var authorizeNetPaymentSettings = await _settingService.LoadSettingAsync<AuthorizeNetPaymentSettings>(storeScope);

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
            await _settingService.SaveSettingOverridablePerStoreAsync(authorizeNetPaymentSettings, x => x.UseSandbox, model.UseSandbox_OverrideForStore, storeScope, false);
            await _settingService.SaveSettingOverridablePerStoreAsync(authorizeNetPaymentSettings, x => x.UseShippingAddressAsBilling, model.UseShippingAddressAsBilling_OverrideForStore, storeScope, false);
            await _settingService.SaveSettingOverridablePerStoreAsync(authorizeNetPaymentSettings, x => x.TransactMode, model.TransactModeId_OverrideForStore, storeScope, false);
            await _settingService.SaveSettingOverridablePerStoreAsync(authorizeNetPaymentSettings, x => x.TransactionKey, model.TransactionKey_OverrideForStore, storeScope, false);
            await _settingService.SaveSettingOverridablePerStoreAsync(authorizeNetPaymentSettings, x => x.LoginId, model.LoginId_OverrideForStore, storeScope, false);
            await _settingService.SaveSettingOverridablePerStoreAsync(authorizeNetPaymentSettings, x => x.AdditionalFee, model.AdditionalFee_OverrideForStore, storeScope, false);
            await _settingService.SaveSettingOverridablePerStoreAsync(authorizeNetPaymentSettings, x => x.AdditionalFeePercentage, model.AdditionalFeePercentage_OverrideForStore, storeScope, false);

            //now clear settings cache
            await _settingService.ClearCacheAsync();

            _notificationService.SuccessNotification(await _localizationService.GetResourceAsync("Admin.Plugins.Saved"));

            return await Configure();
        }

        public async Task<IActionResult> IPNHandler(IpnModel model, IFormCollection form)
        {
            if (!((await _paymentPluginManager.LoadPluginBySystemNameAsync(Defaults.SYSTEM_NAME)) is AuthorizeNetPaymentProcessor processor) || !_paymentPluginManager.IsPluginActive(processor) ||
                !processor.PluginDescriptor.Installed)
                throw new NopException("AuthorizeNet module cannot be loaded");

            var responseCode = form.Keys.Contains("x_response_code") ? form["x_response_code"].ToString() : string.Empty;

            if (responseCode == "1")
            {
                var transactionId = form.Keys.Contains("x_trans_id") ? form["x_trans_id"].ToString() : string.Empty;

                await processor.ProcessRecurringPaymentAsync(transactionId);
            }

            //nothing should be rendered to visitor
            return Content(string.Empty);
        }

        #endregion
    }
}