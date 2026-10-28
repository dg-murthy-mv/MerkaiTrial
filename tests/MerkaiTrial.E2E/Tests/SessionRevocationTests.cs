// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/SessionRevocationTests.cs
//
// VUL-11 (round 105) — expired or revoked sign-ins are refused.
//
//   Signed_out_expired_and_forged_sessions_are_refused
//
// THE WEBSITE (the sign-in cookie)
//   • A copy of the cookie works while the session is live (control), and
//     stops working the moment the person presses Sign out — the server
//     ends the session, it doesn't just ask the browser to forget it.
//   • Signing out in one browser leaves the person's other browser signed in.
//   • 24 hours after sign-in the session is over, however busy it was
//     ("a day later" is made by moving the session's limit into the past).
//   • "Keep me signed in" lasts until that same 24-hour limit — not two weeks.
//
// THE API (the bearer token)
//   A genuine token is accepted (control). Refused, every one:
//   expired; not yet valid; no expiry at all; unsigned ("alg": "none");
//   signed with another algorithm (HS512); signed with another key; a
//   payload changed after signing (another workspace, super admin); a
//   correctly signed token whose NameIdentifier is missing or names someone
//   else (it used to reach a handler and give a 500); the
//   wrong issuer or audience; no signature part; plain rubbish; and a
//   token made from a session that has since been signed out.
//
// Uses a person of its own, so no other test's people are signed out.
// =====================================================================

using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Security")]
public class SessionRevocationTests : E2ETest
{
    private const string Inside = "/Dashboard";
    private const string NameIdentifier = "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier";

    [Test]
    public async Task Signed_out_expired_and_forged_sessions_are_refused()
    {
        var a = World.A;
        var m = Random.Shared.Next(10000, 99999).ToString();
        var problems = new List<string>();

        var person = await Step("A person of this test's own", () =>
            WorldBuilder.AddPersonForTestAsync(a, "sess" + m, "Sasha", "Viewer"));
        var personId = await Db.UserIdAsync(person.Email);

        // ── THE WEBSITE ───────────────────────────────────────────────
        await Step("A copy of the sign-in cookie works while the session is live — and stops the moment they sign out", async () =>
        {
            using var mine = await WebSession.SignedInAsync(person);
            using var copy = mine.Copy();

            var before = await copy.GetAsync(Inside);
            Assert.That(before.ToSignIn, Is.False,
                $"A copy of a live sign-in cookie was refused, so the sign-out check below would prove nothing: {before}");

            var sid = await LiveSessionAsync(personId);
            await mine.SignOutAsync();

            var after = await copy.GetAsync(Inside);
            if (!after.ToSignIn) problems.Add($"After Sign out, a copy of the cookie still opened {Inside}: {after}");

            var own = await mine.GetAsync(Inside);
            if (!own.ToSignIn) problems.Add($"After Sign out, the same browser still opened {Inside}: {own}");

            var row = (await Db.SessionsAsync(personId)).FirstOrDefault(s => s.Id == sid);
            if (row is null) problems.Add("The session row disappeared instead of being ended.");
            else
            {
                if (row.EndedAtUtc is null) problems.Add("Sign out did not end the session on the server (EndedAtUtc is empty).");
                if (row.EndReason != "Signed out") problems.Add($"The session's end reason is '{row.EndReason}', not 'Signed out'.");
            }
        });

        await Step("Signing out in one browser leaves the person's other browser signed in", async () =>
        {
            using var laptop = await WebSession.SignedInAsync(person);
            using var phone = await WebSession.SignedInAsync(person);

            await laptop.SignOutAsync();

            if (!(await laptop.GetAsync(Inside)).ToSignIn) problems.Add("The browser that signed out is still signed in.");
            var other = await phone.GetAsync(Inside);
            if (other.ToSignIn) problems.Add($"Signing out on one browser signed the person out of the other too: {other}");
            await phone.SignOutAsync();
        });

        await Step("A day later (the session's 24-hour limit passed) the cookie is refused, however busy it was", async () =>
        {
            using var s = await WebSession.SignedInAsync(person);
            Assert.That((await s.GetAsync(Inside)).ToSignIn, Is.False, "The new session didn't work, so the expiry below would prove nothing.");

            var sid = await LiveSessionAsync(personId);
            await Db.ExpireSessionAsync(sid);

            var r = await s.GetAsync(Inside);
            if (!r.ToSignIn) problems.Add($"A session past its 24-hour limit still opened {Inside}: {r}");
        });

        await Step("\"Keep me signed in\" lasts until the 24-hour limit, not two weeks", async () =>
        {
            var signedInAt = DateTime.UtcNow;
            using var s = await WebSession.SignedInAsync(person, rememberMe: true);

            var cookie = s.AuthCookie;
            if (cookie is null) { problems.Add("No sign-in cookie was set with \"Keep me signed in\"."); return; }

            if (cookie.Expires == DateTime.MinValue)
                problems.Add("With \"Keep me signed in\" the cookie has no expiry, so it would not survive closing the browser.");
            else if (cookie.Expires.ToUniversalTime() > signedInAt.AddHours(24).AddMinutes(5))
                problems.Add($"With \"Keep me signed in\" the cookie lasts until {cookie.Expires.ToUniversalTime():u} — more than 24 hours.");

            var sid = await LiveSessionAsync(personId);
            var row = (await Db.SessionsAsync(personId)).First(x => x.Id == sid);
            if (row.ExpiresAtUtc - row.CreatedAtUtc > TimeSpan.FromHours(24).Add(TimeSpan.FromMinutes(1)))
                problems.Add($"The session is allowed {row.ExpiresAtUtc - row.CreatedAtUtc} — more than 24 hours.");

            await s.SignOutAsync();
        });

        // ── THE API ──────────────────────────────────────────────────
        var id = await Db.ApiIdentityAsync(person.Email);
        var tenantB = await Db.TenantIdAsync(World.B.Name);
        const string probe = "/api/leads?pageNumber=1&pageSize=1";

        await Step("Control: a genuine token is accepted", async () =>
        {
            var r = await HandMadeJwt.GetAsync(probe, HandMadeJwt.Make(HandMadeJwt.PayloadFor(id)));
            Assert.That(r.Status is 200 or 403, Is.True,
                $"A genuine token was refused ({r}), so the refusals below would prove nothing. Is the WebApi from this run?");
        });

        await Step("Expired, not-yet-valid, never-expiring, unsigned, wrongly signed and altered tokens are all refused", async () =>
        {
            var now = DateTimeOffset.UtcNow;
            var valid = HandMadeJwt.Make(HandMadeJwt.PayloadFor(id));

            var cases = new List<(string What, string Token)>
            {
                ("expired two minutes ago", HandMadeJwt.Make(With(HandMadeJwt.PayloadFor(id), p =>
                    { p["nbf"] = now.AddMinutes(-10).ToUnixTimeSeconds(); p["iat"] = now.AddMinutes(-10).ToUnixTimeSeconds(); p["exp"] = now.AddMinutes(-2).ToUnixTimeSeconds(); }))),
                ("not valid for another ten minutes", HandMadeJwt.Make(With(HandMadeJwt.PayloadFor(id), p =>
                    { p["nbf"] = now.AddMinutes(10).ToUnixTimeSeconds(); p["exp"] = now.AddMinutes(15).ToUnixTimeSeconds(); }))),
                ("with no expiry at all", HandMadeJwt.Make(With(HandMadeJwt.PayloadFor(id), p => p.Remove("exp")))),
                ("signed, but without the person's NameIdentifier", HandMadeJwt.Make(With(HandMadeJwt.PayloadFor(id), p => p.Remove(NameIdentifier)))),
                ("signed, but whose NameIdentifier names someone else", HandMadeJwt.Make(With(HandMadeJwt.PayloadFor(id), p => p[NameIdentifier] = Guid.NewGuid().ToString()))),
                ("unsigned (alg: none)", HandMadeJwt.Make(HandMadeJwt.PayloadFor(id), alg: "none")),
                ("signed with HS512 instead of HS256", HandMadeJwt.Make(HandMadeJwt.PayloadFor(id), alg: "HS512")),
                ("signed with another key", HandMadeJwt.Make(HandMadeJwt.PayloadFor(id), key: new string('k', 64))),
                ("changed after signing to name workspace B", HandMadeJwt.Tamper(valid, p => p["TenantId"] = tenantB.ToString())),
                ("changed after signing to say super admin", HandMadeJwt.Tamper(valid, p => p["IsSuperAdmin"] = "true")),
                ("changed after signing to last a year", HandMadeJwt.Tamper(valid, p => p["exp"] = now.AddYears(1).ToUnixTimeSeconds())),
                ("from another issuer", HandMadeJwt.Make(With(HandMadeJwt.PayloadFor(id), p => p["iss"] = "someone-else"))),
                ("for another audience", HandMadeJwt.Make(With(HandMadeJwt.PayloadFor(id), p => p["aud"] = "another-api"))),
                ("with its signature cut off", string.Join('.', valid.Split('.').Take(2)) + "."),
                ("that is not a token at all", "not-a-token"),
            };

            foreach (var (what, token) in cases)
            {
                var r = await HandMadeJwt.GetAsync(probe, token);
                if (r.Status != 401) problems.Add($"A token {what} got {r} (expected 401).");
                else if (r.Body.Contains("   at ", StringComparison.Ordinal) || r.Body.Contains("Exception", StringComparison.Ordinal))
                    problems.Add($"The refusal of a token {what} carries error details: {ApiResponse.Short(r.Body, 200)}");
            }
        });

        await Step("A token made from a session is refused once that session is signed out", async () =>
        {
            using var s = await WebSession.SignedInAsync(person);
            var sid = await LiveSessionAsync(personId);
            var token = HandMadeJwt.Make(HandMadeJwt.PayloadFor(id, sid));

            var live = await HandMadeJwt.GetAsync(probe, token);
            Assert.That(live.Status is 200 or 403, Is.True,
                $"A token from a live session was refused ({live}), so the check below would prove nothing.");

            await s.SignOutAsync();

            var after = await HandMadeJwt.GetAsync(probe, token);
            if (after.Status != 401) problems.Add($"A token from a signed-out session (still within its 5 minutes) got {after} (expected 401).");

            var forged = HandMadeJwt.Make(HandMadeJwt.PayloadFor(id, Guid.NewGuid()));
            var none = await HandMadeJwt.GetAsync(probe, forged);
            if (none.Status != 401) problems.Add($"A token naming a session that never existed got {none} (expected 401).");
        });

        Assert.That(problems, Is.Empty, $"{problems.Count} problem(s):\n  • " + string.Join("\n  • ", problems));
    }

    /// <summary>The person's newest session that is still live.</summary>
    private static async Task<Guid> LiveSessionAsync(Guid userId)
    {
        var live = (await Db.SessionsAsync(userId))
            .Where(s => s.EndedAtUtc is null && s.ExpiresAtUtc > DateTime.UtcNow)
            .OrderByDescending(s => s.CreatedAtUtc)
            .FirstOrDefault();
        return live?.Id ?? throw new AssertionException(
            "Signing in made no session row in UserSessions. Was Sql/105_UserSessions.sql run on MerkaiTrial_E2E?");
    }

    private static Dictionary<string, object?> With(Dictionary<string, object?> payload, Action<Dictionary<string, object?>> change)
    {
        change(payload);
        return payload;
    }
}
