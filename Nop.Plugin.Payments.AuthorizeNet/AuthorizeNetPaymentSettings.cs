using Nop.Core.Configuration;

namespace Nop.Plugin.Payments.AuthorizeNet
{
    public class AuthorizeNetPaymentSettings : ISettings
    {
        /// <summary>
        /// Gets or sets a value indicating whether to use sandbox environment
        /// </summary>
        public bool UseSandbox { get; set; }

        /// <summary>
        /// Gets or sets the payment processor transaction mode
        /// </summary>
        public TransactMode TransactMode { get; set; }

        /// <summary>
        /// Gets or sets the Authorize.Net transaction key
        /// </summary>
        public string TransactionKey { get; set; }

        /// <summary>
        /// Gets or sets the Authorize.Net login ID
        /// </summary>
        public string LoginId { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether to use a shipping address as a billing address 
        /// </summary>
        public bool UseShippingAddressAsBilling { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether to "additional fee" is specified as percentage. true - percentage, false - fixed value.
        /// </summary>
        public bool AdditionalFeePercentage { get; set; }

        /// <summary>
        /// Gets or sets an additional fee
        /// </summary>
        public decimal AdditionalFee { get; set; }
    }
}
