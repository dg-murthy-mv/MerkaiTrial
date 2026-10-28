// =====================================================================
// WebhookReplayGuard.cs
// Location: MerkaiTrial.Application/Services/Inbound/WebhookReplayGuard.cs
//
// NEW FILE (105 — VUL-19). A signed webhook event is acted on once.
//
// The signature proves Resend sent an event; it does not stop the SAME
// signed request being sent again. Anyone who saw one on its way in (a
// proxy log, a copy of the request) could post it again, unchanged, for as
// long as its timestamp is accepted — five minutes. For received mail that
// already did nothing (the email id is unique), but for delivery events it
// could move a message back: "bounced", then "marked as spam", then the old
// "bounced" again.
//
// Each event carries an id (svix-id) that Resend keeps the same across its
// own retries. This remembers the ids of events already handled, for ten
// minutes — twice the five the timestamp check allows — so a copy inside
// that window is answered "duplicate" and changes nothing, and one outside
// it is refused by the timestamp check.
//
// TAKEN, THEN KEPT OR GIVEN BACK
//   An id is taken when its event starts (so two copies at the same moment
//   cannot both go through) and given back if handling did not finish —
//   "try again later", or an error — because Resend's retry of a failed
//   delivery carries the same id and must be let in.
//
// In memory, one per server. With several servers a copy could reach a
// different one; the timestamp window and the unique email id still hold
// there. A shared table is the step up if this ever runs on more than one.
// =====================================================================

using System.Collections.Concurrent;

namespace MerkaiTrial.Application.Services.Inbound;

public sealed class WebhookReplayGuard
{
    public static readonly TimeSpan Remember = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, DateTime> _seen = new(StringComparer.Ordinal);
    private DateTime _nextSweepUtc = DateTime.MinValue;

    /// <summary>
    /// True when this event id is new and is now taken; false when it was
    /// handled (or is being handled) already.
    /// </summary>
    public bool TryTake(string eventId, DateTime nowUtc)
    {
        Sweep(nowUtc);

        while (true)
        {
            if (_seen.TryAdd(eventId, nowUtc)) return true;

            if (!_seen.TryGetValue(eventId, out var at)) continue;           // removed meanwhile — try again
            if (nowUtc - at < Remember) return false;                       // seen, still remembered

            // Remembered too long ago to matter: the timestamp check would
            // refuse a copy that old anyway. Take it over.
            if (_seen.TryUpdate(eventId, nowUtc, at)) return true;
        }
    }

    /// <summary>Handling did not finish — let Resend's retry of this id in.</summary>
    public void GiveBack(string eventId) => _seen.TryRemove(eventId, out _);

    private void Sweep(DateTime nowUtc)
    {
        if (nowUtc < _nextSweepUtc) return;
        _nextSweepUtc = nowUtc.AddMinutes(1);

        foreach (var (id, at) in _seen)
            if (nowUtc - at >= Remember) _seen.TryRemove(id, out _);
    }
}
