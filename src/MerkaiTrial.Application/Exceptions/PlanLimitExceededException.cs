using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MerkaiTrial.Application.Exceptions
{
    /// <summary>
    /// A create refused because the workspace's plan is full.
    ///
    /// 103 — now an InvalidOperationException, like every other refusal.
    /// Places that catch it by name still do (it is caught before the
    /// general case everywhere); places that only knew "a refusal is an
    /// InvalidOperationException" — the users endpoint, lead conversion —
    /// now answer with its sentence (400) instead of a 500.
    /// </summary>
    public class PlanLimitExceededException : InvalidOperationException
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
