using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MerkaiTrial.Application.Exceptions
{
    public class PlanLimitExceededException : Exception
    {
        public string Resource { get; }
        public int Current { get; }
        public int Limit { get; }

        public PlanLimitExceededException(string resource, int current, int limit)
            : base($"Plan limit reached: you have {current} {resource} " +
                   $"and your plan allows {limit}. Upgrade to add more.")
        {
            Resource = resource;
            Current = current;
            Limit = limit;
        }
    }
}
