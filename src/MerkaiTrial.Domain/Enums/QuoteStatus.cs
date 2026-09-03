using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MerkaiTrial.Domain.Enums
{
    public enum QuoteStatus
    {
        Draft = 0,      // Quote created but not sent
        Sent = 1,       // Quote sent to customer
        Viewed = 2,     // Customer viewed the quote
        Accepted = 3,   // Customer accepted - ready for invoice
        Rejected = 4,   // Customer rejected
        Expired = 5,    // Quote expired
        Revised = 6     // Quote revised (new version created)
    }
}
