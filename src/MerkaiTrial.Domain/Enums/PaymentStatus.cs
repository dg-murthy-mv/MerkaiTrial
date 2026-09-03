using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MerkaiTrial.Domain.Enums
{
    public enum PaymentStatus
    {
        Pending = 0,     // Payment initiated but not confirmed
        Captured = 1,    // Payment successfully captured
        Failed = 2,      // Payment failed
        Refunded = 3,    // Payment refunded
        Cancelled = 4    // Payment cancelled
    }
}
