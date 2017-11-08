using System.Collections.Generic;

namespace Geta.Resurs.Checkout.Callbacks
{
    public class CallbackData
    {
        public CallbackEventType EventType { get; set; }
        public string PaymentId { get; set; }
        public IDictionary<string, string> PropertiesDictionary { get; set; }
    }
}