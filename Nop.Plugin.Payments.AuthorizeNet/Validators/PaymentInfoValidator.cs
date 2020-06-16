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
                .WithMessage(localizationService.GetResource("Payment.CardholderName.Required"));

            RuleFor(x => x.CardNumber)
                .IsCreditCard()
                .WithMessage(localizationService.GetResource("Payment.CardNumber.Wrong"));

            RuleFor(x => x.CardCode)
                .Matches(@"^[0-9]{3,4}$")
                .WithMessage(localizationService.GetResource("Payment.CardCode.Wrong"));

            RuleFor(x => x.ExpireMonth)
                .NotEmpty()
                .WithMessage(localizationService.GetResource("Payment.ExpireMonth.Required"));

            RuleFor(x => x.ExpireYear)
                .NotEmpty()
                .WithMessage(localizationService.GetResource("Payment.ExpireYear.Required"));
        }

        #endregion
    }
}