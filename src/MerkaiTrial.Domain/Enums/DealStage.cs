using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MerkaiTrial.Domain.Enums
{
    public enum DealStage
    {
        Discovery = 0,      // 20% probability
        Qualification=1,
        Proposal = 2,       // 40% probability
        Negotiation = 3,    // 60% probability
        ClosedWon = 4,      // 100% probability
        ClosedLost = 5      // 0% probability
    }
}
