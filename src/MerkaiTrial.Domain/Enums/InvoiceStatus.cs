using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MerkaiTrial.Domain.Enums
{
    public enum InvoiceStatus
    {
        Draft = 0,           // Created but not sent
        Sent = 1,            // Sent to customer
        Viewed = 2,          // Customer viewed invoice
        PartiallyPaid = 3,   // Partial payment received
        Paid = 4,            // Fully paid
        Overdue = 5,         // Past due date, unpaid
        Cancelled = 6,        // Invoice cancelled
        Unpaid=7
    }
}
