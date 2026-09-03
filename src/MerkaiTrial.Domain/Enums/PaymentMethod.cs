using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MerkaiTrial.Domain.Enums
{
    public enum PaymentMethod
    {
        BankTransfer = 0,
        CreditCard = 1,
        DebitCard = 2,
        Cash = 3,
        Check = 4,
        PromptPay = 5,
        PayPal = 6,
        Stripe = 7,
        Other = 99
    }
}
