// =====================================================================
// FILE: MerkaiTrial.Application/Security/AttemptLimiter.cs
//
// NEW (104). "How many times has this address tried this, lately?" — for
// the three pages anyone on the internet can use without signing in.
//
//   Sign in          20 FAILED attempts per minute from one network
//                    address. (On top of the account lock: 5 wrong
//                    passwords locks that account for 15 minutes.)
//   Forgot password  10 requests per hour from one network address. (On
//                    top of 3 emails per account per hour.)
//   Set password     10 FAILED attempts (a link that isn't valid) per 15
//                    minutes from one network address.
//   Customer links   106: 20 WRONG links (/q/… or /i/… that lead nowhere)
//                    per 10 minutes from one network address. Genuine
//                    links keep working everywhere else.
//
// Only FAILURES count for sign-in and set-password: the threat is guessing
// (passwords, links), and counting successes too would lock out an office
// where everyone signs in at 9:00 from one address. Every forgot-password
// request counts, because every one may send an email.
//
// A FIXED WINDOW per (what, address): the count starts at the first
// attempt and clears when the window ends. Kept in this server's memory —
// a restart clears it, and with several servers behind a load balancer
// each counts its own (move it to a shared store then). Old entries are
// swept as new ones arrive, so it never grows without bound.
// =====================================================================

using System.Collections.Concurrent;

namespace MerkaiTrial.Application.Security;

public sealed record AttemptRule(string Name, int Limit, TimeSpan Window);

public static class AttemptRules
{
    public static readonly AttemptRule SignInFailures   = new("sign-in",         20, TimeSpan.FromMinutes(1));
    public static readonly AttemptRule ResetRequests    = new("forgot-password", 10, TimeSpan.FromHours(1));
    public static readonly AttemptRule SetPasswordFails = new("set-password",    10, TimeSpan.FromMinutes(15));
    public static readonly AttemptRule PublicLinkMisses = new("public-link",     20, TimeSpan.FromMinutes(10));   // 106
}

public sealed class AttemptLimiter
{
    private sealed class Window
    {
        public DateTime StartUtc;
        public int Count;
    }

    private readonly ConcurrentDictionary<string, Window> _windows = new();
    private DateTime _nextSweepUtc = DateTime.MinValue;
    private readonly Func<DateTime> _clock;

    public AttemptLimiter() : this(() => DateTime.UtcNow) { }
    public AttemptLimiter(Func<DateTime> clock) => _clock = clock;

    /// <summary>True when <paramref name="address"/> has used up this rule's attempts for now.</summary>
    public bool IsOver(AttemptRule rule, string? address)
    {
        var w = Current(rule, address);
        if (w is null) return false;
        lock (w) return w.Count >= rule.Limit;
    }

    /// <summary>Counts one attempt (a failure, or a request — whichever the rule counts).</summary>
    public void Count(AttemptRule rule, string? address)
    {
        var now = _clock();
        Sweep(now);
        var w = _windows.GetOrAdd(Key(rule, address), _ => new Window { StartUtc = now });
        lock (w)
        {
            if (now - w.StartUtc >= rule.Window) { w.StartUtc = now; w.Count = 0; }
            w.Count++;
        }
    }

    /// <summary>Counts the attempt and says whether it was still within the limit.</summary>
    public bool TryCount(AttemptRule rule, string? address)
    {
        if (IsOver(rule, address)) return false;
        Count(rule, address);
        return true;
    }

    private Window? Current(AttemptRule rule, string? address)
    {
        if (!_windows.TryGetValue(Key(rule, address), out var w)) return null;
        lock (w)
        {
            if (_clock() - w.StartUtc >= rule.Window) { w.Count = 0; w.StartUtc = _clock(); }
        }
        return w;
    }

    private static string Key(AttemptRule rule, string? address) =>
        rule.Name + "|" + (string.IsNullOrWhiteSpace(address) ? "unknown" : address);

    private void Sweep(DateTime now)
    {
        if (now < _nextSweepUtc) return;
        _nextSweepUtc = now.AddMinutes(5);
        foreach (var (key, w) in _windows)
        {
            // The longest window is an hour; anything older is finished.
            if (now - w.StartUtc > TimeSpan.FromHours(2)) _windows.TryRemove(key, out _);
        }
    }
}
