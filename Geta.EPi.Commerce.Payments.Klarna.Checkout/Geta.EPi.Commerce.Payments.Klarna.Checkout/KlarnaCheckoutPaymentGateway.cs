﻿using System;
using System.Linq;
using Geta.EPi.Commerce.Payments.Klarna.Checkout.Extensions;
using Geta.Klarna.Checkout;
using Mediachase.Commerce.Orders;
using Mediachase.Commerce.Orders.Exceptions;
using Mediachase.Commerce.Plugins.Payment;
using EPiServer.Logging;
using EPiServer.ServiceLocation;
using Geta.Klarna.Checkout.Models;
using OrderStatus = Mediachase.Commerce.Orders.OrderStatus;

namespace Geta.EPi.Commerce.Payments.Klarna.Checkout
{
    public class KlarnaCheckoutPaymentGateway : AbstractPaymentGateway
    {
        private static Injected<IPostProcessPayment> _postProcessPayment;
        private static IPostProcessPayment PostProcessPayment
        {
            get { return _postProcessPayment.Service; }
        }

        private static readonly ILogger Logger = LogManager.GetLogger(typeof(KlarnaCheckoutPaymentGateway));

        public override bool ProcessPayment(Payment payment, ref string message)
        {
            Logger.Debug("Klarna checkout gateway. Processing Payment ....");
            VerifyConfiguration();

            var orderGroup = payment.Parent.Parent;
            var transactionType = payment.TransactionType.ToUpper();

            var orderApiClient = new OrderApiClient(Int32.Parse(KlarnaSettings.MerchantId), KlarnaSettings.Secret, KlarnaSettings.CurrentLocale, KlarnaSettings.IsProduction);

            try
            {
                switch (transactionType)
                {
                    case "CAPTURE":
                        {
                            var reservation = GetReservation(payment);

                            if (string.IsNullOrEmpty(reservation))
                            {
                                var errorMessage =
                                    "CAPTURE operation KlarnaCheckoutPaymentGateway failed. Metafield 'Reservation' on KlarnaPayment is empty.";
                                Logger.Error(errorMessage);
                                throw new Exception(errorMessage);
                            }

                            var orderForm = orderGroup.OrderForms[0];

                            // We will include cart items (in case of partial shipment)
                            var shipment = orderForm.Shipments[0];  // only 1 shipment is valid
                            var cartItems = orderForm.LineItems.Select(item => item.ToCartItem(true)).ToList();
                            cartItems.AddRange(shipment.ToCartItems(true));

                            var purchaseOrder = (orderGroup as PurchaseOrder);

                            if (purchaseOrder == null)
                            {
                                var errorMessage =
                                    "CAPTURE operation KlarnaCheckoutPaymentGateway failed. Uanble to cast orderGroup to PurchaseOrder.";
                                Logger.Error(errorMessage);
                                throw new Exception(errorMessage);
                            }


                            var trackingNr = purchaseOrder.TrackingNumber;

                            string infoMsg = string.Format("KlarnaCheckoutPaymentGateway: Activating reservation {0}. Transaction id: {1}. Tracking number: {2}.",
                                reservation, payment.TransactionID, trackingNr);
                            Logger.Debug(infoMsg);

                            var response = orderApiClient.Activate(reservation, payment.TransactionID, trackingNr, cartItems);
                            payment.Status = response.IsSuccess ? PaymentStatus.Processed.ToString() : PaymentStatus.Failed.ToString();
                            if (response.IsSuccess)
                            {
                                orderGroup.OrderNotes.Add(new OrderNote
                                {
                                    Title = "Invoice number",
                                    Detail = response.InvoiceNumber
                                });

                                // we need to save invoice number incase of refunds later
                                purchaseOrder[MetadataConstants.InvoiceId] = response.InvoiceNumber;
                                orderGroup.AcceptChanges();
                                PostProcessPayment.PostCapture(response, payment);
                            }
                            else
                            {
                                PostProcessPayment.PostCapture(response, payment);
                                Logger.Error(string.Format("Capture failed for order {0} with reservation {1}. Error message: {2}",
                                    trackingNr, reservation, response.ErrorMessage));
                                throw new Exception(response.ErrorMessage);
                            }

                            return response.IsSuccess;
                        }
                    case "VOID":
                        {
                            var reservation = GetReservation(payment);

                            if (string.IsNullOrEmpty(reservation))
                            {
                                var errorMessage =
                                    "VOID operation KlarnaCheckoutPaymentGateway failed. Metafield 'Reservation' on KlarnaPayment is empty.";
                                Logger.Error(errorMessage);
                                throw new Exception(errorMessage);
                            }

                            Logger.Debug(string.Format("Cancel reservation called with reservation {0}. Transaction id is {1}.", reservation, payment.TransactionID));

                            var cancelResult = orderApiClient.CancelReservation(reservation);
                            if (cancelResult.IsSuccess)
                            {
                                orderGroup.Status = OrderStatus.Cancelled.ToString();
                                payment.Status = PaymentStatus.Processed.ToString();
                            }
                            else
                            {
                                payment.Status = PaymentStatus.Failed.ToString();
                            }

                            orderGroup.AcceptChanges();
                            PostProcessPayment.PostAnnul(cancelResult.IsSuccess, payment);

                            if (cancelResult.IsSuccess == false)
                            {
                                var errorMessage = string.Format("VOID operation KlarnaCheckoutPaymentGateway failed. Error is {0}.", cancelResult.ErrorMessage);
                                Logger.Error(errorMessage);
                                throw new Exception(cancelResult.ErrorMessage);
                            }

                            return cancelResult.IsSuccess;

                        }
                    case "CREDIT":
                        {
                            var purchaseOrder = orderGroup as PurchaseOrder;
                            if (purchaseOrder != null)
                            {
                                var invoiceNumber = GetInvoiceId(purchaseOrder);

                                if (string.IsNullOrEmpty(invoiceNumber))
                                {
                                    var errorMessage =
                                        "CREDIT operation on KlarnaCheckoutPaymentGateway failed. Metafield 'InvoiceNumber' is empty.";
                                    Logger.Error(errorMessage);
                                    throw new Exception(errorMessage);
                                }

                                var returnFormToProcess =
                                    purchaseOrder.ReturnOrderForms.FirstOrDefault(
                                        p => p.Status == ReturnFormStatus.AwaitingCompletion.ToString() && p.Total == payment.Amount);

                                if (returnFormToProcess == null)
                                {
                                    payment.Status = PaymentStatus.Failed.ToString();
                                    PostProcessPayment.PostCredit(new RefundResponse() { IsSuccess = false, ErrorMessage = "No return forms to process." }, payment);
                                    return false;
                                }

                                // Determine if this is full refund, in that case we will call CreditInvoice
                                // If payment.Amount = captured amount then do full refund
                                var capturedAmount = orderGroup.OrderForms[0].Payments
                                    .Where(p => p.TransactionType == "Capture" & p.Status == PaymentStatus.Processed.ToString())
                                    .Sum(p => p.Amount);

                                var result = new RefundResponse();

                                if (capturedAmount == payment.Amount) // full refund
                                {
                                    result = orderApiClient.CreditInvoice(invoiceNumber);
                                }
                                else
                                {
                                    var returnItems = returnFormToProcess.LineItems.Select(item => item.ToCartItem()).ToList();
                                    // if shipment is part of returnForm, then we will return shipping cost as well
                                    var shipment = returnFormToProcess.Shipments[0];
                                    if (shipment != null && shipment.ShippingTotal > 0)
                                        returnItems.AddRange(shipment.ToCartItems());

                                    result = orderApiClient.HandleRefund(invoiceNumber, returnItems);
                                }

                                payment.Status = result.IsSuccess ? PaymentStatus.Processed.ToString() : PaymentStatus.Failed.ToString();
                                orderGroup.AcceptChanges();

                                PostProcessPayment.PostCredit(result, payment);

                                if (result.IsSuccess == false)
                                {
                                    Logger.Error(result.ErrorMessage);
                                    throw new Exception(result.ErrorMessage);
                                }

                                
                                return result.IsSuccess;
                            }
                            return false;

                        }
                }

            }
            catch (Exception exception)
            {
                Logger.Error("Process payment failed with error: " + exception.Message, exception);
                throw;
            }

            return true;
        }

        private string GetInvoiceId(PurchaseOrder purchaseOrder)
        {
            string invoiceId = purchaseOrder.GetStringValue(MetadataConstants.InvoiceId, string.Empty);
            if (string.IsNullOrEmpty(invoiceId))
            {
                return purchaseOrder.GetStringValue(MetadataConstants.InvoiceNumber, string.Empty);
            }
            return invoiceId;
        }

        private string GetReservation(Payment payment)
        {
            string reservationId = payment.GetStringValue(MetadataConstants.ReservationId, string.Empty);
            if (string.IsNullOrEmpty(reservationId))
            {
                return payment.GetStringValue(MetadataConstants.ReservationField, string.Empty);
            }
            return reservationId;
        }



        private ProviderSettings _klarnaSettings;
        internal ProviderSettings KlarnaSettings
        {
            get
            {
                if (_klarnaSettings == null)
                {
                    _klarnaSettings = new ProviderSettings(
                        bool.Parse(Settings[KlarnaConstants.IsProduction]),
                        Settings[KlarnaConstants.MerchantId],
                        Settings[KlarnaConstants.Secret],
                        Settings[KlarnaConstants.Locale],
                        bool.Parse(Settings[KlarnaConstants.EnableOrganizationCheckout]));
                }
                Logger.Debug(string.Format("Active Klarna merchant id is {0}. Is testing environment: {1}", _klarnaSettings.MerchantId, !_klarnaSettings.IsProduction));
                return _klarnaSettings;
            }
        }


        private void VerifyConfiguration()
        {
            if (string.IsNullOrEmpty(Settings[KlarnaConstants.MerchantId]))
            {
                throw new PaymentException(PaymentException.ErrorType.ConfigurationError, "",
                                           "Payment configuration is not valid. Missing payment provider merchant identification nr.");
            }

            if (string.IsNullOrEmpty(Settings[KlarnaConstants.Secret]))
            {
                throw new PaymentException(PaymentException.ErrorType.ConfigurationError, "",
                                           "Payment configuration is not valid. Missing payment provider merchant secret.");
            }

            if (string.IsNullOrEmpty(Settings[KlarnaConstants.Locale]))
            {
                throw new PaymentException(PaymentException.ErrorType.ConfigurationError, "",
                                           "Payment method configuration is not valid. Missing payment Locale.");
            }

            Logger.Debug("Payment method configuration verified.");
        }
    }
}
