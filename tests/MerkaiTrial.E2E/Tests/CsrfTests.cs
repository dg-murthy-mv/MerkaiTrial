// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/CsrfTests.cs
//
// VUL-07 (round 104) — cross-site request forgery.
//
//   Forged_cross_site_requests_are_rejected
//
// The website uses a sign-in cookie, so the danger is another website
// making the signed-in person's browser send a form here. Two defences,
// both checked:
//
//   1. Every form post must carry the page's anti-forgery token, which
//      another website can't read. Posting "add a note" to a lead —
//        • with the page's own token        → the note is saved (control);
//        • with no token                    → refused (400), nothing saved;
//        • with another person's token      → refused (400), nothing saved;
//        • from another website's page      → nothing saved.
//   2. The sign-in cookie itself: HttpOnly (script can't read it), Secure
//      (never sent over plain http) and SameSite=Lax (not sent with
//      another website's form posts).
//
// Also: nothing changes on a GET — "?handler=Delete" in an address (an
// <img> on another site could request it) deletes nothing — and the API
// ignores the website's cookie altogether: it answers only to a bearer
// token, which a browser never adds by itself.
// =====================================================================

using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Security")]
public class CsrfTests : E2ETest
{
    [Test]
    public async Task Forged_cross_site_requests_are_rejected()
    {
        var a = World.A;
        var tenantA = await Db.TenantIdAsync(a.Name);
        var apiA = await Api.AsAsync(a.Admin);
        var m = Random.Shared.Next(10000, 99999).ToString();
        var problems = new List<string>();

        var leadId = await Step("A lead of workspace A's to post notes to", async () =>
        {
            var r = await apiA.PostAsync("/api/leads", new
            {
                tenantId = tenantA, fullName = "Csrf Lead " + m, email = World.EmailFor("A", "csrf" + m),
                phone = "+9198" + m + "555", companyName = "Csrf Co " + m, estimatedValue = 1000m, currency = "INR",
            });
            Assert.That(r.IsSuccess, Is.True, $"Adding the lead failed: {r}");
            return ApiIds.IdOf(r);
        });

        var page = await SignedInAsync(a.Admin);
        await page.GotoAsync($"/Leads/Detail/{leadId}");
        var ownToken = await PageHttp.FormTokenAsync(page);
        var notePath = $"/Leads/Detail/{leadId}?handler=AddNote";

        Task<Guid?> NoteAsync(string text) => Db.ChildIdAsync("LeadNotes", "LeadId", leadId, "Note", text);

        await Step("Control: the note form with the page's own token saves the note", async () =>
        {
            var text = "Csrf own " + m;
            var r = await PageHttp.FetchAsync(page, "POST", notePath, Form(ownToken, text), follow: false);
            Assert.That(r.Status is 0 or (>= 200 and < 400), Is.True, $"The genuine form post failed: {r}");
            Assert.That(await NoteAsync(text), Is.Not.Null,
                "The genuine form post saved no note, so the refusals below would prove nothing. " + r);
        });

        await Step("Without a token: refused with 400, nothing saved", async () =>
        {
            var text = "Csrf none " + m;
            var r = await PageHttp.FetchAsync(page, "POST", notePath, new[] { KeyValuePair.Create("NoteInput.Note", text) }, follow: false);
            if (r.Status != 400) problems.Add($"A form post with no anti-forgery token got {r} (expected 400).");
            if (await NoteAsync(text) is not null) problems.Add("A form post with no anti-forgery token SAVED the note.");
        });

        await Step("With another person's token: refused with 400, nothing saved", async () =>
        {
            var other = await SignedInAsync(World.B.Admin);
            await other.GotoAsync("/Dashboard");
            var foreignToken = await other.EvaluateAsync<string>(
                "async () => { const r = await fetch('/Leads/Create'); const t = await r.text(); " +
                "const m = t.match(/name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"/); return m ? m[1] : ''; }");
            Assert.That(foreignToken, Is.Not.Empty, "Couldn't get another person's form token to try.");

            var text = "Csrf foreign " + m;
            var r = await PageHttp.FetchAsync(page, "POST", notePath, Form(foreignToken, text), follow: false);
            if (r.Status != 400) problems.Add($"A form post with another person's anti-forgery token got {r} (expected 400).");
            if (await NoteAsync(text) is not null) problems.Add("A form post with another person's anti-forgery token SAVED the note.");
        });

        await Step("From another website's page (a form that posts itself here): nothing saved", async () =>
        {
            var text = "Csrf cross " + m;
            var evil = await page.Context.NewPageAsync();
            await evil.GotoAsync("about:blank");
            await evil.SetContentAsync(
                $@"<html><body>
                    <form id=""f"" method=""post"" action=""{E2ESettings.BaseUrl}{notePath}"">
                      <input name=""NoteInput.Note"" value=""{text}"">
                    </form>
                    <script>document.getElementById('f').submit();</script>
                  </body></html>");
            await evil.WaitForTimeoutAsync(2500);
            if (await NoteAsync(text) is not null) problems.Add("Another website's self-posting form SAVED a note on the signed-in person's lead.");
            await evil.CloseAsync();
        });

        await Step("The sign-in cookie is HttpOnly, Secure and SameSite=Lax", async () =>
        {
            var cookies = await page.Context.CookiesAsync(new[] { E2ESettings.BaseUrl });
            var auth = cookies.FirstOrDefault(c => c.Name == "MerkaiTrial.Auth");
            if (auth is null) { problems.Add("The sign-in cookie (MerkaiTrial.Auth) wasn't found."); return; }
            if (!auth.HttpOnly) problems.Add("The sign-in cookie can be read by script (not HttpOnly).");
            if (!auth.Secure) problems.Add("The sign-in cookie can travel over plain http (not Secure).");
            if (auth.SameSite == SameSiteAttribute.None) problems.Add("The sign-in cookie goes with other websites' requests (SameSite=None).");
        });

        await Step("A GET never changes anything: \"?handler=Delete\" in the address deletes nothing", async () =>
        {
            var r = await PageHttp.FetchAsync(page, "GET", $"/Leads/Detail/{leadId}?handler=Delete");
            if (r.Status >= 500) problems.Add($"GET …?handler=Delete → HTTP {r.Status}.");
            if (await Db.IsRowDeletedAsync("Leads", leadId) != false) problems.Add("A GET with ?handler=Delete DELETED the lead.");
        });

        await Step("The API ignores the website's cookie: a cookie-only request is refused", async () =>
        {
            var r = await PageHttp.FetchAsync(page, "GET", E2ESettings.ApiBaseUrl + "/api/leads?pageNumber=1&pageSize=1");
            // The browser blocks reading another origin's answer (no CORS) —
            // which is itself the point — so a status of 0 / an error is a
            // pass too. What must not happen is a 200 with leads in it.
            if (r.Status == 200 && r.Text.Contains("Csrf Lead " + m, StringComparison.Ordinal))
                problems.Add("The API answered a request that carried only the website's cookie, with workspace A's leads.");
        });

        Assert.That(problems, Is.Empty, $"{problems.Count} problem(s):\n  • " + string.Join("\n  • ", problems));
    }

    private static KeyValuePair<string, string>[] Form(string token, string text) => new[]
    {
        KeyValuePair.Create("__RequestVerificationToken", token),
        KeyValuePair.Create("NoteInput.Note", text),
    };
}
