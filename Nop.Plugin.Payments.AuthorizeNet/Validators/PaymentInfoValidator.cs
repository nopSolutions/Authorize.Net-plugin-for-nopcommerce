using FluentValidation;
using Nop.Plugin.Payments.AuthorizeNet.Models;
using Nop.Services.Localization;
using Nop.Web.Framework.Validators;

namespace Nop.Plugin.Payments.AuthorizeNet.Validators
{
    public class PaymentInfoValidator : BaseNopValidator<PaymentInfoModel>
    {
        #region Ctor

        public PaymentInfoValidator(ILocalizationService localizationService)
        {
            RuleFor(x => x.CardholderName)
                .NotEmpty()
                .WithMessageAwait(localizationService.GetResourceAsync("Payment.CardholderName.Required"));

            RuleFor(x => x.CardNumber)
                .IsCreditCard()
                .WithMessageAwait(localizationService.GetResourceAsync("Payment.CardNumber.Wrong"));

            RuleFor(x => x.CardCode)
                .Matches(@"^[0-9]{3,4}$")
                .WithMessageAwait(localizationService.GetResourceAsync("Payment.CardCode.Wrong"));

            RuleFor(x => x.ExpireMonth)
                .NotEmpty()
                .WithMessageAwait(localizationService.GetResourceAsync("Payment.ExpireMonth.Required"));

            RuleFor(x => x.ExpireYear)
                .NotEmpty()
                .WithMessageAwait(localizationService.GetResourceAsync("Payment.ExpireYear.Required"));
        }

        #endregion
    }
}