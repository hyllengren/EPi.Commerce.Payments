﻿using Geta.Epi.Commerce.Payments.Resurs.Checkout.Extensions;
using Geta.Resurs.Checkout;
using Mediachase.Commerce.Orders.Dto;
using Mediachase.Web.Console.Interfaces;

namespace Geta.EPi.Payments.Resurs.CommerceManager.Apps.Order.Payments.Plugins.ResursBankCheckout
{
    public partial class ConfigurePayment : System.Web.UI.UserControl, IGatewayControl
    {
        public string ValidationGroup { get; set; }

        public void LoadObject(object dto)
        {
            var paymentMethod = dto as PaymentMethodDto;

            if (paymentMethod == null)
            {
                return;
            }

            txtUserName.Text = paymentMethod.GetParameter(ResursConstants.UserName, string.Empty);
            txtPassword.Text = paymentMethod.GetParameter(ResursConstants.Password, string.Empty);
            chkIncludeShippingCost.Checked = GetBool(paymentMethod, ResursConstants.IncludeShippingCost);
        }

        public void SaveChanges(object dto)
        {
            if (!Visible)
            {
                return;
            }

            var paymentMethod = dto as PaymentMethodDto;
            if (paymentMethod == null)
            {
                return;
            }

            paymentMethod.SetParameter(ResursConstants.UserName, txtUserName.Text);
            paymentMethod.SetParameter(ResursConstants.Password, txtPassword.Text);
            paymentMethod.SetParameter(ResursConstants.IncludeShippingCost, chkIncludeShippingCost.Checked ? "true" : "false");
        }

        private bool GetBool(PaymentMethodDto paymentMethod, string fieldName, string defaultValue = "false")
        {
            return bool.TryParse(paymentMethod.GetParameter(fieldName, defaultValue), out bool includeShippingCosts) && includeShippingCosts;
        }
    }
}