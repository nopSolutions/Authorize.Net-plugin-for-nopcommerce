using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AuthorizeNet.Api.Contracts.V1;
using AuthorizeNet.Api.Controllers;
using AuthorizeNet.Api.Controllers.Bases;
using Microsoft.AspNetCore.Http;
using Nop.Core;
using Nop.Core.Domain.Catalog;
using Nop.Core.Domain.Directory;
using Nop.Core.Domain.Orders;
using Nop.Core.Domain.Payments;
using Nop.Plugin.Payments.AuthorizeNet.Models;
using Nop.Plugin.Payments.AuthorizeNet.Validators;
using Nop.Services.Common;
using Nop.Services.Configuration;
using Nop.Services.Customers;
using Nop.Services.Directory;
using Nop.Services.Localization;
using Nop.Services.Logging;
using Nop.Services.Orders;
using Nop.Services.Payments;
using Nop.Services.Plugins;
using Nop.Services.Security;

using AuthorizeNetSDK = AuthorizeNet;

namespace Nop.Plugin.Payments.AuthorizeNet
{
    /// <summary>
    /// AuthorizeNet payment processor
    /// </summary>
    public class AuthorizeNetPaymentProcessor : BasePlugin, IPaymentMethod
    {
        #region Fields

        private readonly AuthorizeNetPaymentSettings _authorizeNetPaymentSettings;
        private readonly CurrencySettings _currencySettings;
        private readonly IAddressService _addressService;
        private readonly ICountryService _countryService;
        private readonly ICurrencyService _currencyService;
        private readonly ICustomerService _customerService;
        private readonly IEncryptionService _encryptionService;
        private readonly ILocalizationService _localizationService;
        private readonly ILogger _logger;
        private readonly IOrderProcessingService _orderProcessingService;
        private readonly IOrderService _orderService;
        private readonly IPaymentService _paymentService;
        private readonly ISettingService _settingService;
        private readonly IStateProvinceService _stateProvinceService;
        private readonly IWebHelper _webHelper;

        #endregion

        #region Ctor

        public AuthorizeNetPaymentProcessor(AuthorizeNetPaymentSettings authorizeNetPaymentSettings,
            CurrencySettings currencySettings,
            IAddressService addressService,
            ICountryService countryService,
            ICurrencyService currencyService,
            ICustomerService customerService,
            IEncryptionService encryptionService,
            ILocalizationService localizationService,
            ILogger logger,
            IOrderProcessingService orderProcessingService,
            IOrderService orderService,
            IPaymentService paymentService,
            ISettingService settingService,
            IStateProvinceService stateProvinceService,
            IWebHelper webHelper)
        {
            _authorizeNetPaymentSettings = authorizeNetPaymentSettings;
            _currencySettings = currencySettings;
            _addressService = addressService;
            _countryService = countryService;
            _currencyService = currencyService;
            _customerService = customerService;
            _encryptionService = encryptionService;
            _localizationService = localizationService;
            _logger = logger;
            _orderProcessingService = orderProcessingService;
            _orderService = orderService;
            _paymentService = paymentService;
            _settingService = settingService;
            _stateProvinceService = stateProvinceService;
            _webHelper = webHelper;
        }

        #endregion

        #region Utilities

        /// <summary>
        /// Gets API response
        /// </summary>
        /// <param name="controller">Transaction controller</param>
        /// <param name="errors">List of errors</param>
        /// <returns>API response</returns>
        protected static createTransactionResponse GetApiResponse(createTransactionController controller, IList<string> errors)
        {
            var response = controller.GetApiResponse();
            
            if (response != null)
            {
                if (response.transactionResponse?.errors != null)
                {
                    foreach (var transactionResponseError in response.transactionResponse.errors) 
                        errors.Add($"Error #{transactionResponseError.errorCode}: {transactionResponseError.errorText}");

                    return null;
                }

                if (response.transactionResponse != null && response.messages.resultCode == messageTypeEnum.Ok)
                    switch (response.transactionResponse.responseCode)
                    {
                        case "1":
                            return response;
                        case "2":
                            var description = response.transactionResponse.messages.Any()
                                ? response.transactionResponse.messages.First().description
                                : string.Empty;
                            errors.Add($"Declined ({response.transactionResponse.responseCode}: {description})".TrimEnd(':', ' '));
                            return null;
                    }
                else if (response.transactionResponse != null && response.messages.resultCode == messageTypeEnum.Error)
                    if (response.messages?.message != null && response.messages.message.Any())
                    {
                        var message = response.messages.message.First();

                        errors.Add($"Error #{message.code}: {message.text}");
                        return null;
                    }
            }
            else
            {
                var error = controller.GetErrorResponse();
                if (error?.messages?.message != null && error.messages.message.Any())
                {
                    var message = error.messages.message.First();

                    errors.Add($"Error #{message.code}: {message.text}");
                    return null;
                }
            }

            var controllerResult = controller.GetResults().FirstOrDefault();

            if (controllerResult?.StartsWith("I00001", StringComparison.InvariantCultureIgnoreCase) ?? false)
                return null;

            const string unknownError = "Authorize.NET unknown error";
            errors.Add(string.IsNullOrEmpty(controllerResult) ? unknownError : $"{unknownError} ({controllerResult})");
            
            return null;
        }

        /// <summary>
        /// Gets address
        /// </summary>
        /// <param name="addressId">Address ID</param>
        /// <returns>
        /// A task that represents the asynchronous operation
        /// The task result contains the address
        /// </returns>
        protected virtual async Task<customerAddressType> GetTransactionRequestAddressAsync(int addressId)
        {
            var address = await _addressService.GetAddressByIdAsync(addressId);

            if (address == null)
                return new customerAddressType();

            var transactionRequestAddress = new customerAddressType
            {
                firstName = CommonHelper.EnsureMaximumLength(address.FirstName, 50),
                lastName = CommonHelper.EnsureMaximumLength(address.LastName, 50),
                email = CommonHelper.EnsureMaximumLength(address.Email, 50),
                address = CommonHelper.EnsureMaximumLength(address.Address1, 60),
                city = CommonHelper.EnsureMaximumLength(address.City, 40),
                zip = CommonHelper.EnsureMaximumLength(address.ZipPostalCode, 20)
            };

            if (!string.IsNullOrEmpty(address.Company))
                transactionRequestAddress.company = CommonHelper.EnsureMaximumLength(address.Company, 50);

            if (address.StateProvinceId.HasValue)
                transactionRequestAddress.state = (await _stateProvinceService.GetStateProvinceByAddressAsync(address))?.Abbreviation;

            if (address.CountryId.HasValue)
                transactionRequestAddress.country = (await _countryService.GetCountryByIdAsync(address.CountryId.Value))?.TwoLetterIsoCode;

            return transactionRequestAddress;
        }

        /// <summary>
        /// Gets an address to recurring transaction request
        /// </summary>
        /// <param name="addressId">Address ID</param>
        /// <returns>
        /// A task that represents the asynchronous operation
        /// The task result contains the address
        /// </returns>
        protected virtual async Task<nameAndAddressType> GetRecurringTransactionRequestAddressAsync(int addressId)
        {
            var address = await _addressService.GetAddressByIdAsync(addressId);

            if (address == null)
                return new nameAndAddressType();

            var transactionRequestAddress = new nameAndAddressType
            {
                firstName = CommonHelper.EnsureMaximumLength(address.FirstName, 50),
                lastName = CommonHelper.EnsureMaximumLength(address.LastName, 50),
                address = CommonHelper.EnsureMaximumLength(address.Address1, 60),
                city = CommonHelper.EnsureMaximumLength(address.City, 40),
                zip = CommonHelper.EnsureMaximumLength(address.ZipPostalCode, 20)
            };

            if (!string.IsNullOrEmpty(address.Company))
                transactionRequestAddress.company = CommonHelper.EnsureMaximumLength(address.Company, 50);

            if (address.StateProvinceId.HasValue)
                transactionRequestAddress.state = (await _stateProvinceService.GetStateProvinceByAddressAsync(address))?.Abbreviation;

            if (address.CountryId.HasValue)
                transactionRequestAddress.country = (await _countryService.GetCountryByIdAsync(address.CountryId.Value))?.TwoLetterIsoCode;

            return transactionRequestAddress;
        }

        /// <summary>
        /// Configure Authorize.Net API
        /// </summary>
        protected virtual void PrepareAuthorizeNet()
        {
            ApiOperationBase<ANetApiRequest, ANetApiResponse>.RunEnvironment = _authorizeNetPaymentSettings.UseSandbox
                ? AuthorizeNetSDK.Environment.SANDBOX
                : AuthorizeNetSDK.Environment.PRODUCTION;

            // define the merchant information (authentication / transaction id)
            ApiOperationBase<ANetApiRequest, ANetApiResponse>.MerchantAuthentication = new merchantAuthenticationType
            {
                name = _authorizeNetPaymentSettings.LoginId,
                ItemElementName = ItemChoiceType.transactionKey,
                Item = _authorizeNetPaymentSettings.TransactionKey
            };
        }

        /// <summary>
        /// Gets transaction details
        /// </summary>
        /// <param name="transactionId">Transaction ID</param>
        /// <returns>
        /// A task that represents the asynchronous operation
        /// The task result contains the transaction details
        /// </returns>
        protected virtual async Task<TransactionDetails> GetTransactionDetailsAsync(string transactionId)
        {
            PrepareAuthorizeNet();

            var request = new getTransactionDetailsRequest { transId = transactionId };

            // instantiate the controller that will call the service
            var controller = new getTransactionDetailsController(request);
            controller.Execute();

            // get the response from the service (errors contained if any)
            var response = controller.GetApiResponse();

            if (response?.messages == null)
            {
                await _logger.ErrorAsync($"Authorize.NET unknown error (transactionId: {transactionId})");

                return new TransactionDetails { IsOk = false };
            }

            var transactionDetails = new TransactionDetails
            {
                IsOk = response.messages.resultCode == messageTypeEnum.Ok,
                Message = response.messages.message.FirstOrDefault()
            };

            if (response.transaction == null)
                await _logger.ErrorAsync($"Authorize.NET: Transaction data is missing (transactionId: {transactionId})");
            else
            {
                transactionDetails.TransactionStatus = response.transaction.transactionStatus;
                transactionDetails.OrderDescriptions = response.transaction.order.description.Split('#');
                transactionDetails.TransactionType = response.transaction.transactionType;
            }

            return transactionDetails;
        }

        #endregion

        #region Methods

        /// <summary>
        /// Process a payment
        /// </summary>
        /// <param name="processPaymentRequest">Payment info required for an order processing</param>
        /// <returns>
        /// A task that represents the asynchronous operation
        /// The task result contains the process payment result
        /// </returns>
        public async Task<ProcessPaymentResult> ProcessPaymentAsync(ProcessPaymentRequest processPaymentRequest)
        {
            var result = new ProcessPaymentResult();

            PrepareAuthorizeNet();

            var creditCard = new creditCardType
            {
                cardNumber = processPaymentRequest.CreditCardNumber,
                expirationDate =
                    processPaymentRequest.CreditCardExpireMonth.ToString("D2") + processPaymentRequest.CreditCardExpireYear,
                cardCode = processPaymentRequest.CreditCardCvv2
            };

            //standard api call to retrieve response
            var paymentType = new paymentType { Item = creditCard };

            var transactionType = _authorizeNetPaymentSettings.TransactMode switch
            {
                TransactMode.Authorize => transactionTypeEnum.authOnlyTransaction,
                TransactMode.AuthorizeAndCapture => transactionTypeEnum.authCaptureTransaction,
                _ => throw new NopException("Not supported transaction mode")
            };

            var customer = await _customerService.GetCustomerByIdAsync(processPaymentRequest.CustomerId);

            var billTo = _authorizeNetPaymentSettings.UseShippingAddressAsBilling && customer?.ShippingAddressId != null ? await GetTransactionRequestAddressAsync(customer.ShippingAddressId.Value) : await GetTransactionRequestAddressAsync(customer?.BillingAddressId ?? 0);

            var transactionRequest = new transactionRequestType
            {
                transactionType = transactionType.ToString(),
                amount = Math.Round(processPaymentRequest.OrderTotal, 2),
                payment = paymentType,
                currencyCode = (await _currencyService.GetCurrencyByIdAsync(_currencySettings.PrimaryStoreCurrencyId)).CurrencyCode,
                billTo = billTo,
                customerIP = _webHelper.GetCurrentIpAddress(),
                order = new orderType
                {
                    //x_invoice_num is 20 chars maximum. Hence we also pass x_description
                    invoiceNumber = processPaymentRequest.OrderGuid.ToString().Substring(0, 20),
                    description = $"Full order #{processPaymentRequest.OrderGuid}"
                }
            };

            if (customer?.ShippingAddressId != null && !_authorizeNetPaymentSettings.UseShippingAddressAsBilling)
            {
                var shipTo = await GetTransactionRequestAddressAsync(customer.ShippingAddressId.Value);
                transactionRequest.shipTo = shipTo;
            }

            var request = new createTransactionRequest { transactionRequest = transactionRequest };

            // instantiate the controller that will call the service
            var controller = new createTransactionController(request);
            controller.Execute();

            // get the response from the service (errors contained if any)
            var response = GetApiResponse(controller, result.Errors);

            //validate
            if (response == null)
                return result;

            switch (_authorizeNetPaymentSettings.TransactMode)
            {
                case TransactMode.Authorize:
                    result.AuthorizationTransactionId = response.transactionResponse.transId;
                    result.AuthorizationTransactionCode =
                        $"{response.transactionResponse.transId},{response.transactionResponse.authCode}";
                    break;
                case TransactMode.AuthorizeAndCapture:
                    result.CaptureTransactionId =
                        $"{response.transactionResponse.transId},{response.transactionResponse.authCode}";
                    break;
            }

            result.AuthorizationTransactionResult =
                $"Approved ({response.transactionResponse.responseCode}: {response.transactionResponse.messages[0].description})";
            result.AvsResult = response.transactionResponse.avsResultCode;
            result.NewPaymentStatus = _authorizeNetPaymentSettings.TransactMode == TransactMode.Authorize ? PaymentStatus.Authorized : PaymentStatus.Paid;

            return result;
        }

        /// <summary>
        /// Post process payment (used by payment gateways that require redirecting to a third-party URL)
        /// </summary>
        /// <param name="postProcessPaymentRequest">Payment info required for an order processing</param>
        /// <returns>A task that represents the asynchronous operation</returns>
        public Task PostProcessPaymentAsync(PostProcessPaymentRequest postProcessPaymentRequest)
        {
            //nothing
            return Task.CompletedTask;
        }

        /// <summary>
        /// Gets additional handling fee
        /// </summary>
        /// <param name="cart">Shopping cart</param>
        /// <returns>
        /// A task that represents the asynchronous operation
        /// The task result contains the additional handling fee
        /// </returns>
        public async Task<decimal> GetAdditionalHandlingFeeAsync(IList<ShoppingCartItem> cart)
        {
            var result = await _paymentService.CalculateAdditionalFeeAsync(cart,
                _authorizeNetPaymentSettings.AdditionalFee, _authorizeNetPaymentSettings.AdditionalFeePercentage);
           
            return result;
        }

        /// <summary>
        /// Returns a value indicating whether payment method should be hidden during checkout
        /// </summary>
        /// <param name="cart">Shopping cart</param>
        /// <returns>
        /// A task that represents the asynchronous operation
        /// The task result contains the rue - hide; false - display.
        /// </returns>
        public Task<bool> HidePaymentMethodAsync(IList<ShoppingCartItem> cart)
        {
            //you can put any logic here
            //for example, hide this payment method if all products in the cart are downloadable
            //or hide this payment method if current customer is from certain country
            return Task.FromResult(false);
        }

        /// <summary>
        /// Captures payment
        /// </summary>
        /// <param name="capturePaymentRequest">Capture payment request</param>
        /// <returns>
        /// A task that represents the asynchronous operation
        /// The task result contains the capture payment result
        /// </returns>
        public async Task<CapturePaymentResult> CaptureAsync(CapturePaymentRequest capturePaymentRequest)
        {
            var result = new CapturePaymentResult();

            PrepareAuthorizeNet();

            var codes = capturePaymentRequest.Order.AuthorizationTransactionCode.Split(',');
            var transactionRequest = new transactionRequestType
            {
                transactionType = transactionTypeEnum.priorAuthCaptureTransaction.ToString(),
                amount = Math.Round(capturePaymentRequest.Order.OrderTotal, 2),
                refTransId = codes[0],
                currencyCode = (await _currencyService.GetCurrencyByIdAsync(_currencySettings.PrimaryStoreCurrencyId)).CurrencyCode
            };

            var request = new createTransactionRequest { transactionRequest = transactionRequest };

            //instantiate the controller that will call the service
            var controller = new createTransactionController(request);
            controller.Execute();

            //get the response from the service (errors contained if any)
            var response = GetApiResponse(controller, result.Errors);

            //validate
            if (response == null)
                return result;

            result.CaptureTransactionId =
                $"{response.transactionResponse.transId},{response.transactionResponse.authCode}";
            result.CaptureTransactionResult =
                $"Approved ({response.transactionResponse.responseCode}: {response.transactionResponse.messages[0].description})";
            result.NewPaymentStatus = PaymentStatus.Paid;

            return result;
        }

        /// <summary>
        /// Refunds a payment
        /// </summary>
        /// <param name="refundPaymentRequest">Request</param>
        /// <returns>
        /// A task that represents the asynchronous operation
        /// The task result contains the result
        /// </returns>
        public async Task<RefundPaymentResult> RefundAsync(RefundPaymentRequest refundPaymentRequest)
        {
            var result = new RefundPaymentResult();

            var codes = (string.IsNullOrEmpty(refundPaymentRequest.Order.CaptureTransactionId)
                ? refundPaymentRequest.Order.AuthorizationTransactionCode
                : refundPaymentRequest.Order.CaptureTransactionId).Split(',');

            var transactionId = codes[0];

            var transactionDetails = await GetTransactionDetailsAsync(transactionId);

            if (transactionDetails.TransactionStatus == "capturedPendingSettlement")
            {
                if (refundPaymentRequest.IsPartialRefund)
                {
                    result.Errors.Add("This transaction is supports only full refund");

                    return result;
                }

                var voidResult = await VoidAsync(new VoidPaymentRequest
                {
                    Order = refundPaymentRequest.Order
                });

                if (!voidResult.Success)
                {
                    foreach (var voidResultError in voidResult.Errors) 
                        result.Errors.Add(voidResultError);

                    return result;
                }

                result.NewPaymentStatus = PaymentStatus.Refunded;

                return result;
            }
            
            PrepareAuthorizeNet();

            var maskedCreditCardNumberDecrypted = _encryptionService.DecryptText(refundPaymentRequest.Order.MaskedCreditCardNumber);

            if (string.IsNullOrEmpty(maskedCreditCardNumberDecrypted) || maskedCreditCardNumberDecrypted.Length < 4)
            {
                result.AddError("Last four digits of Credit Card Not Available");
                return result;
            }

            var lastFourDigitsCardNumber = maskedCreditCardNumberDecrypted.Substring(maskedCreditCardNumberDecrypted.Length - 4);
            var creditCard = new creditCardType
            {
                cardNumber = lastFourDigitsCardNumber,
                expirationDate = "XXXX"
            };

            var transactionRequest = new transactionRequestType
            {
                transactionType = transactionTypeEnum.refundTransaction.ToString(),
                amount = Math.Round(refundPaymentRequest.AmountToRefund, 2),
                refTransId = transactionId,
                currencyCode = (await _currencyService.GetCurrencyByIdAsync(_currencySettings.PrimaryStoreCurrencyId)).CurrencyCode,

                order = new orderType
                {
                    //x_invoice_num is 20 chars maximum. Hence we also pass x_description
                    invoiceNumber = refundPaymentRequest.Order.OrderGuid.ToString().Substring(0, 20),
                    description = $"Full order #{refundPaymentRequest.Order.OrderGuid}"
                },

                payment = new paymentType { Item = creditCard }
            };

            var request = new createTransactionRequest { transactionRequest = transactionRequest };

            // instantiate the controller that will call the service
            var controller = new createTransactionController(request);
            controller.Execute();

            GetApiResponse(controller, result.Errors);
            result.NewPaymentStatus = PaymentStatus.PartiallyRefunded;

            return result;
        }

        /// <summary>
        /// Voids a payment
        /// </summary>
        /// <param name="voidPaymentRequest">Request</param>
        /// <returns>
        /// A task that represents the asynchronous operation
        /// The task result contains the result
        /// </returns>
        public Task<VoidPaymentResult> VoidAsync(VoidPaymentRequest voidPaymentRequest)
        {
            var result = new VoidPaymentResult();

            PrepareAuthorizeNet();

            var maskedCreditCardNumberDecrypted = _encryptionService.DecryptText(voidPaymentRequest.Order.MaskedCreditCardNumber);

            if (string.IsNullOrEmpty(maskedCreditCardNumberDecrypted) || maskedCreditCardNumberDecrypted.Length < 4)
            {
                result.AddError("Last four digits of Credit Card Not Available");
                return Task.FromResult(result);
            }

            var lastFourDigitsCardNumber = maskedCreditCardNumberDecrypted.Substring(maskedCreditCardNumberDecrypted.Length - 4);
            var expirationDate = voidPaymentRequest.Order.CardExpirationMonth + voidPaymentRequest.Order.CardExpirationYear;

            if (!expirationDate.Any())
                expirationDate = "XXXX";

            var creditCard = new creditCardType
            {
                cardNumber = lastFourDigitsCardNumber,
                expirationDate = expirationDate
            };

            var codes = (string.IsNullOrEmpty(voidPaymentRequest.Order.CaptureTransactionId) ? voidPaymentRequest.Order.AuthorizationTransactionCode : voidPaymentRequest.Order.CaptureTransactionId).Split(',');
            var transactionRequest = new transactionRequestType
            {
                transactionType = transactionTypeEnum.voidTransaction.ToString(),
                refTransId = codes[0],
                payment = new paymentType { Item = creditCard }
            };

            var request = new createTransactionRequest { transactionRequest = transactionRequest };

            // instantiate the controller that will call the service
            var controller = new createTransactionController(request);
            controller.Execute();

            var response = GetApiResponse(controller, result.Errors);

            //validate
            if (response == null)
                return Task.FromResult(result);

            result.NewPaymentStatus = PaymentStatus.Voided;

            return Task.FromResult(result);
        }

        /// <summary>
        /// Process recurring payment
        /// </summary>
        /// <param name="processPaymentRequest">Payment info required for an order processing</param>
        /// <returns>
        /// A task that represents the asynchronous operation
        /// The task result contains the process payment result
        /// </returns>
        public async Task<ProcessPaymentResult> ProcessRecurringPaymentAsync(ProcessPaymentRequest processPaymentRequest)
        {
            var result = new ProcessPaymentResult();

            PrepareAuthorizeNet();
            
            var creditCard = new creditCardType
            {
                cardNumber = processPaymentRequest.CreditCardNumber,
                expirationDate =
                    processPaymentRequest.CreditCardExpireMonth.ToString("D2") + processPaymentRequest.CreditCardExpireYear,
                cardCode = processPaymentRequest.CreditCardCvv2
            };

            //standard api call to retrieve response
            var paymentType = new paymentType { Item = creditCard };

            var customer = await _customerService.GetCustomerByIdAsync(processPaymentRequest.CustomerId);

            var billTo = _authorizeNetPaymentSettings.UseShippingAddressAsBilling && customer?.ShippingAddressId != null ? await GetRecurringTransactionRequestAddressAsync(customer.ShippingAddressId ?? 0) : await GetRecurringTransactionRequestAddressAsync(customer?.BillingAddressId ?? 0);

            var dtNow = DateTime.UtcNow;

            // Interval can't be updated once a subscription is created.
            var paymentScheduleInterval = new paymentScheduleTypeInterval();
            switch (processPaymentRequest.RecurringCyclePeriod)
            {
                case RecurringProductCyclePeriod.Days:
                    paymentScheduleInterval.length = Convert.ToInt16(processPaymentRequest.RecurringCycleLength);
                    paymentScheduleInterval.unit = ARBSubscriptionUnitEnum.days;
                    break;
                case RecurringProductCyclePeriod.Weeks:
                    paymentScheduleInterval.length = Convert.ToInt16(processPaymentRequest.RecurringCycleLength * 7);
                    paymentScheduleInterval.unit = ARBSubscriptionUnitEnum.days;
                    break;
                case RecurringProductCyclePeriod.Months:
                    paymentScheduleInterval.length = Convert.ToInt16(processPaymentRequest.RecurringCycleLength);
                    paymentScheduleInterval.unit = ARBSubscriptionUnitEnum.months;
                    break;
                case RecurringProductCyclePeriod.Years:
                    paymentScheduleInterval.length = Convert.ToInt16(processPaymentRequest.RecurringCycleLength * 12);
                    paymentScheduleInterval.unit = ARBSubscriptionUnitEnum.months;
                    break;
                default:
                    throw new NopException("Not supported cycle period");
            }

            var paymentSchedule = new paymentScheduleType
            {
                startDate = new DateTime(dtNow.Year, dtNow.Month, dtNow.Day),
                totalOccurrences = Convert.ToInt16(processPaymentRequest.RecurringTotalCycles),
                interval = paymentScheduleInterval
            };
            
            var subscriptionType = new ARBSubscriptionType
            {
                name = processPaymentRequest.OrderGuid.ToString(),
                amount = Math.Round(processPaymentRequest.OrderTotal, 2),
                payment = paymentType,
                billTo = billTo,
                paymentSchedule = paymentSchedule,
                customer = new customerType
                {
                    email = (await _addressService.GetAddressByIdAsync(customer?.BillingAddressId ?? 0))?.Email
                },
                
                order = new orderType
                {
                    //x_invoice_num is 20 chars maximum. Hence we also pass x_description
                    invoiceNumber = processPaymentRequest.OrderGuid.ToString().Substring(0, 20),
                    description = $"Recurring payment #{processPaymentRequest.OrderGuid}"
                }
            };

            if (customer?.ShippingAddressId != null && !_authorizeNetPaymentSettings.UseShippingAddressAsBilling)
            {
                var shipTo = await GetRecurringTransactionRequestAddressAsync(customer.ShippingAddressId ?? 0);
                subscriptionType.shipTo = shipTo;
            }

            var request = new ARBCreateSubscriptionRequest { subscription = subscriptionType };

            // instantiate the controller that will call the service
            var controller = new ARBCreateSubscriptionController(request);
            controller.Execute();

            // get the response from the service (errors contained if any)
            var response = controller.GetApiResponse();

            //validate
            if (response != null && response.messages.resultCode == messageTypeEnum.Ok)
            {
                result.SubscriptionTransactionId = response.subscriptionId;
                result.AuthorizationTransactionCode = response.refId;
                result.AuthorizationTransactionResult = $"Approved ({response.refId}: {response.subscriptionId})";
            }
            else if (response != null)
                foreach (var responseMessage in response.messages.message)
                    result.AddError($"Error processing recurring payment #{responseMessage.code}: {responseMessage.text}");
            else
                result.AddError("Authorize.NET unknown error");

            return result;
        }

        /// <summary>
        /// Process recurring payment
        /// </summary>
        /// <param name="transactionId">AuthorizeNet transaction ID</param>
        /// <returns>A task that represents the asynchronous operation</returns>
        public async Task ProcessRecurringPaymentAsync(string transactionId)
        {
            var transactionDetails = await GetTransactionDetailsAsync(transactionId);

            if (transactionDetails.TransactionStatus == "refundTransaction")
                return;

            var orderDescriptions = transactionDetails.OrderDescriptions;

            if (orderDescriptions.Length < 2)
            {
                await _logger.ErrorAsync($"Authorize.NET: Missing order GUID (transactionId: {transactionId})");
                return;
            }

            if (orderDescriptions[0].Contains("Full order"))
                return;

            var order = await _orderService.GetOrderByGuidAsync(new Guid(orderDescriptions[1]));

            if (order == null)
            {
                await _logger.ErrorAsync($"Authorize.NET: Order cannot be loaded (order GUID: {orderDescriptions[1]})");
                return;
            }

            var recurringPayments = await _orderService.SearchRecurringPaymentsAsync(initialOrderId: order.Id);

            foreach (var rp in recurringPayments)
                if (transactionDetails.IsOk)
                {
                    var recurringPaymentHistory = await _orderService.GetRecurringPaymentHistoryAsync(rp);
                    var orders = (await _orderService.GetOrdersByIdsAsync(recurringPaymentHistory.Select(rph => rph.OrderId).ToArray())).ToList();

                    var transactionsIds = new List<string>();
                    transactionsIds.AddRange(orders.Select(o => o.AuthorizationTransactionId).Where(tId => !string.IsNullOrEmpty(tId)));
                    transactionsIds.AddRange(orders.Select(o => o.CaptureTransactionId).Where(tId => !string.IsNullOrEmpty(tId)));

                    //skip the re-processing of transactions
                    if (transactionsIds.Contains(transactionId))
                        continue;

                    var newPaymentStatus = transactionDetails.TransactionType == "authOnlyTransaction" ? PaymentStatus.Authorized : PaymentStatus.Paid;

                    if (recurringPaymentHistory.Count == 0)
                    {
                        //first payment
                        var rph = new RecurringPaymentHistory
                        {
                            RecurringPaymentId = rp.Id,
                            OrderId = order.Id,
                            CreatedOnUtc = DateTime.UtcNow
                        };

                        await _orderService.InsertRecurringPaymentHistoryAsync(rph);

                        var initialOrder = await _orderService.GetOrderByIdAsync(rp.InitialOrderId);

                        if (newPaymentStatus == PaymentStatus.Authorized)
                            initialOrder.AuthorizationTransactionId = transactionId;
                        else
                            initialOrder.CaptureTransactionId = transactionId;

                        await _orderService.UpdateRecurringPaymentAsync(rp);
                    }
                    else
                    {
                        //next payments
                        var processPaymentResult = new ProcessPaymentResult
                        {
                            NewPaymentStatus = newPaymentStatus
                        };

                        if (newPaymentStatus == PaymentStatus.Authorized)
                            processPaymentResult.AuthorizationTransactionId = transactionId;
                        else
                            processPaymentResult.CaptureTransactionId = transactionId;

                        await _orderProcessingService.ProcessNextRecurringPaymentAsync(rp, processPaymentResult);
                    }
                }
                else
                {
                    var processPaymentResult = new ProcessPaymentResult();
                    processPaymentResult.AuthorizationTransactionId = processPaymentResult.CaptureTransactionId = transactionId;
                    processPaymentResult.RecurringPaymentFailed = true;
                    processPaymentResult.Errors.Add(
                        $"Authorize.Net Error: {transactionDetails.Message?.code} - {transactionDetails.Message?.text} (transactionId: {transactionId})");
                    await _orderProcessingService.ProcessNextRecurringPaymentAsync(rp, processPaymentResult);
                }
        }

        /// <summary>
        /// Cancels a recurring payment
        /// </summary>
        /// <param name="cancelPaymentRequest">Request</param>
        /// <returns>
        /// A task that represents the asynchronous operation
        /// The task result contains the result
        /// </returns>
        public Task<CancelRecurringPaymentResult> CancelRecurringPaymentAsync(CancelRecurringPaymentRequest cancelPaymentRequest)
        {
            var result = new CancelRecurringPaymentResult();

            PrepareAuthorizeNet();

            var request = new ARBCancelSubscriptionRequest { subscriptionId = cancelPaymentRequest.Order.SubscriptionTransactionId };
            var controller = new ARBCancelSubscriptionController(request);
            controller.Execute();

            var response = controller.GetApiResponse();

            //validate
            if (response != null && response.messages.resultCode == messageTypeEnum.Ok)
                return Task.FromResult(result);

            if (response != null)
                foreach (var responseMessage in response.messages.message)
                    result.AddError($"Error processing recurring payment #{responseMessage.code}: {responseMessage.text}");
            else
                result.AddError("Authorize.NET unknown error");

            return Task.FromResult(result);
        }

        /// <summary>
        /// Gets a value indicating whether customers can complete a payment after order is placed but not completed (for redirection payment methods)
        /// </summary>
        /// <param name="order">Order</param>
        /// <returns>
        /// A task that represents the asynchronous operation
        /// The task result contains the result
        /// </returns>
        public Task<bool> CanRePostProcessPaymentAsync(Order order)
        {
            if (order == null)
                throw new ArgumentNullException(nameof(order));
            
            //it's not a redirection payment method. So we always return false
            return Task.FromResult(false);
        }

        /// <summary>
        /// Validate payment form
        /// </summary>
        /// <param name="form">The parsed form values</param>
        /// <returns>
        /// A task that represents the asynchronous operation
        /// The task result contains the list of validating errors
        /// </returns>
        public Task<IList<string>> ValidatePaymentFormAsync(IFormCollection form)
        {
            var warnings = new List<string>();

            //validate
            var validator = new PaymentInfoValidator(_localizationService);
            var model = new PaymentInfoModel
            {
                CardholderName = form["CardholderName"],
                CardNumber = form["CardNumber"],
                CardCode = form["CardCode"],
                ExpireMonth = form["ExpireMonth"],
                ExpireYear = form["ExpireYear"]
            };

            var validationResult = validator.Validate(model);

            if (!validationResult.IsValid)
                warnings.AddRange(validationResult.Errors.Select(error => error.ErrorMessage));

            return Task.FromResult<IList<string>>(warnings);
        }

        /// <summary>
        /// Get payment information
        /// </summary>
        /// <param name="form">The parsed form values</param>
        /// <returns>
        /// A task that represents the asynchronous operation
        /// The task result contains the payment info holder
        /// </returns>
        public Task<ProcessPaymentRequest> GetPaymentInfoAsync(IFormCollection form)
        {
            var paymentInfo = new ProcessPaymentRequest
            {
                //CreditCardType is not used by Authorize.NET
                CreditCardName = form["CardholderName"],
                CreditCardNumber = form["CardNumber"],
                CreditCardExpireMonth = int.Parse(form["ExpireMonth"]),
                CreditCardExpireYear = int.Parse(form["ExpireYear"]),
                CreditCardCvv2 = form["CardCode"]
            };

            return Task.FromResult(paymentInfo);
        }

        /// <summary>
        /// Gets a view component for displaying plugin in public store ("payment info" checkout step)
        /// </summary>
        public string GetPublicViewComponentName()
        {
            return Defaults.PAYMENT_INFO_VIEW_COMPONENT_NAME;
        }

        public override string GetConfigurationPageUrl()
        {
            return $"{_webHelper.GetStoreLocation()}Admin/AuthorizeNet/Configure";
        }

        /// <summary>
        /// Install plugin
        /// </summary>
        /// <returns>A task that represents the asynchronous operation</returns>
        public override async Task InstallAsync()
        {
            //settings
            var settings = new AuthorizeNetPaymentSettings
            {
                UseSandbox = true,
                TransactMode = TransactMode.Authorize,
                TransactionKey = "123",
                LoginId = "456"
            };
            await _settingService.SaveSettingAsync(settings);

            //locales
            await _localizationService.AddLocaleResourceAsync(new Dictionary<string, string>
            {
                ["Plugins.Payments.AuthorizeNet.Notes"] = "If you're using this gateway, ensure that your primary store currency is supported by Authorize.NET.",
                ["Plugins.Payments.AuthorizeNet.Fields.UseSandbox"] = "Use Sandbox",
                ["Plugins.Payments.AuthorizeNet.Fields.UseSandbox.Hint"] = "Check to enable Sandbox (testing environment).",
                ["Plugins.Payments.AuthorizeNet.Fields.UseShippingAddressAsBilling"] = "Use shipping address.",
                ["Plugins.Payments.AuthorizeNet.Fields.UseShippingAddressAsBilling.Hint"] = "Check if you want to use the shipping address as a billing address.",
                ["Plugins.Payments.AuthorizeNet.Fields.TransactModeValues"] = "Transaction mode",
                ["Plugins.Payments.AuthorizeNet.Fields.TransactModeValues.Hint"] = "Choose transaction mode.",
                ["Plugins.Payments.AuthorizeNet.Fields.TransactionKey"] = "Transaction key",
                ["Plugins.Payments.AuthorizeNet.Fields.TransactionKey.Hint"] = "Specify transaction key.",
                ["Plugins.Payments.AuthorizeNet.Fields.LoginId"] = "Login ID",
                ["Plugins.Payments.AuthorizeNet.Fields.LoginId.Hint"] = "Specify login identifier.",
                ["Plugins.Payments.AuthorizeNet.Fields.AdditionalFee"] = "Additional fee",
                ["Plugins.Payments.AuthorizeNet.Fields.AdditionalFee.Hint"] = "Enter additional fee to charge your customers.",
                ["Plugins.Payments.AuthorizeNet.Fields.AdditionalFeePercentage"] = "Additional fee. Use percentage",
                ["Plugins.Payments.AuthorizeNet.Fields.AdditionalFeePercentage.Hint"] = "Determines whether to apply a percentage additional fee to the order total. If not enabled, a fixed value is used.",
                ["Plugins.Payments.AuthorizeNet.PaymentMethodDescription"] = "Pay by credit / debit card"
            });

            await base.InstallAsync();
        }

        /// <summary>
        /// Uninstall plugin
        /// </summary>
        /// <returns>A task that represents the asynchronous operation</returns>
        public override async Task UninstallAsync()
        {
            //settings
            await _settingService.DeleteSettingAsync<AuthorizeNetPaymentSettings>();

            //locales
            await _localizationService.DeleteLocaleResourcesAsync("Plugins.Payments.AuthorizeNet");

            await base.UninstallAsync();
        }

        /// <summary>
        /// Gets a payment method description that will be displayed on checkout pages in the public store
        /// </summary>
        /// <remarks>
        /// return description of this payment method to be display on "payment method" checkout step. good practice is to make it localizable
        /// for example, for a redirection payment method, description may be like this: "You will be redirected to PayPal site to complete the payment"
        /// </remarks>
        /// <returns>A task that represents the asynchronous operation</returns>
        public async Task<string> GetPaymentMethodDescriptionAsync()
        {
            return await _localizationService.GetResourceAsync("Plugins.Payments.AuthorizeNet.PaymentMethodDescription");
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets a value indicating whether capture is supported
        /// </summary>
        public bool SupportCapture => true;

        /// <summary>
        /// Gets a value indicating whether partial refund is supported
        /// </summary>
        public bool SupportPartiallyRefund => true;

        /// <summary>
        /// Gets a value indicating whether refund is supported
        /// </summary>
        public bool SupportRefund => true;

        /// <summary>
        /// Gets a value indicating whether void is supported
        /// </summary>
        public bool SupportVoid => true;

        /// <summary>
        /// Gets a recurring payment type of payment method
        /// </summary>
        public RecurringPaymentType RecurringPaymentType => RecurringPaymentType.Automatic;

        /// <summary>
        /// Gets a payment method type
        /// </summary>
        public PaymentMethodType PaymentMethodType => PaymentMethodType.Standard;

        /// <summary>
        /// Gets a value indicating whether we should display a payment information page for this plugin
        /// </summary>
        public bool SkipPaymentInfo => false;

        #endregion

        #region Nested classes

        protected partial class TransactionDetails
        {
            public bool IsOk { get; set; }

            public string TransactionStatus { get; internal set; }

            public string[] OrderDescriptions { get; internal set; }

            public messagesTypeMessage Message { get; internal set; }

            public string TransactionType { get; set; }
        }

        #endregion
    }
}
