// =====================================================================
// UserSession.cs
// Location: MerkaiTrial.Domain/Entities/UserSession.cs
//
// NEW FILE (105 — VUL-11). One row per sign-in.
//
// WHY
//   The sign-in cookie used to be the whole session: signing out only
//   asked the browser to forget it. A copy of the cookie (taken from a
//   shared computer, a proxy log, a stolen laptop) went on working for up
//   to 8 hours after "Sign out", longer if it was kept busy, because
//   nothing on the server knew the session had ended.
//
//   Now each sign-in writes a row here and the cookie carries its id
//   (the "sid" claim). Every request checks the row:
//     • EndedAtUtc set      → signed out (or ended by the app) — refused;
//     • ExpiresAtUtc passed → the 24-hour limit — refused;
//     • missing             → refused.
//   Signing out ends THIS row only, so the person's other browsers and
//   devices stay signed in.
//
// WHOSE SESSION
//   The person who typed the password. During View As the cookie names
//   the person being viewed, but the session is still the super admin's,
//   so UserId here is the real user (the RealUserId claim when present).
//
// No TenantId on purpose: a session belongs to a person, and the cookie
// check reads this table before there is a tenant on the request. The
// row is only ever looked up by its own id.
// =====================================================================

namespace MerkaiTrial.Domain.Entities;

public static class SessionEndReason
{
    public const string SignedOut = "Signed out";
    public const string Replaced  = "Replaced";
}

public class UserSession
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The person who signed in (the super admin during View As).</summary>
    public Guid UserId { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>The hard limit: 24 hours after sign-in, however busy the session is.</summary>
    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>When the session was ended (Sign out). NULL while it is live.</summary>
    public DateTime? EndedAtUtc { get; set; }

    /// <summary>Why it ended — SessionEndReason. NULL while live.</summary>
    public string? EndReason { get; set; }

    /// <summary>The network address it was started from. For the person's own record only.</summary>
    public string? IpAddress { get; set; }

    /// <summary>The browser it was started from, cut to 256 characters.</summary>
    public string? UserAgent { get; set; }

    public bool IsLive(DateTime nowUtc) => EndedAtUtc == null && ExpiresAtUtc > nowUtc;
}
