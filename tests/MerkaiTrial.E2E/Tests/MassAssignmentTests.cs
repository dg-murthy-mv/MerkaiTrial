// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/MassAssignmentTests.cs
//
// VUL-04 (round 104) — mass assignment.
//
//   Protected_properties_cannot_be_set_through_the_API
//
// Workspace A's ADMIN (so no permission can explain a refusal) creates and
// edits records through the API, adding every property a caller must never
// choose for themselves. Each record is then read from the DATABASE: the
// extra properties must have had no effect.
//
//   Lead     id, status, converted, score, created when / by, deleted
//   Deal     created by; created straight into Won (skipping "Won needs
//            an accepted quote") — refused (round 104)
//   Quote    number, status Accepted, total
//   Invoice  number, status Paid, balance
//   Person   super admin, "must change password", the security stamp
//
// Round 104 found two of these open: a deal could be CREATED as Won (the
// stage came from the request and "rules aren't checked on create"), and
// its CreatedBy was whatever the request said. A lead edit's Score was
// taken from the request. All three are closed.
// =====================================================================

using System.Text.Json;
using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Security")]
public class MassAssignmentTests : E2ETest
{
    [Test]
    public async Task Protected_properties_cannot_be_set_through_the_API()
    {
        var a = World.A;
        var tenantId = await Db.TenantIdAsync(a.Name);
        var admin = await Api.AsAsync(a.Admin);
        await Api.EnsureTokensWorkAsync(admin);
        var adminId = await Db.UserIdAsync(a.Admin.Email);
        var m = Random.Shared.Next(10000, 99999).ToString();
        var problems = new List<string>();

        // ── Lead: create ───────────────────────────────────────────
        var chosenId = Guid.NewGuid();
        var (leadId, controlStatus) = await Step("A lead sent with its own id, status, conversion, score, author, date and \"deleted\"", async () =>
        {
            var control = await admin.PostAsync("/api/leads", Lead(tenantId, "Plain " + m, m));
            Assert.That(control.IsSuccess, Is.True, $"Adding a plain lead failed: {control}");
            var plainStatus = await Db.ValueAsync("Leads", "Status", ApiIds.IdOf(control));

            var r = await admin.PostAsync("/api/leads", new
            {
                tenantId, fullName = "Mass " + m, email = World.EmailFor("A", "mass" + m), phone = "+9198" + m + "222",
                companyName = "Mass Co " + m, estimatedValue = 1000m, currency = "INR",
                id = chosenId, status = "Converted", isConverted = true, score = 99,
                createdAtUtc = "2001-01-01T00:00:00Z", createdBy = "Mallory", isDeleted = true,
                convertedAtUtc = "2001-01-01T00:00:00Z", dealId = Guid.NewGuid(),
            });
            Assert.That(r.IsSuccess, Is.True, $"Adding the lead failed: {r}");
            return (ApiIds.IdOf(r), plainStatus);
        });

        await Step("…none of it stuck", async () =>
        {
            if (leadId == chosenId) problems.Add("Lead: the caller chose its id.");
            Expect(problems, "Lead status", await Db.ValueAsync("Leads", "Status", leadId), controlStatus);
            Expect(problems, "Lead converted", await Db.ValueAsync("Leads", "IsConverted", leadId), "False");
            Expect(problems, "Lead deleted", await Db.ValueAsync("Leads", "IsDeleted", leadId), "False");
            Expect(problems, "Lead author", await Db.ValueAsync("Leads", "CreatedBy", leadId), adminId.ToString());
            if (await Db.ValueAsync("Leads", "Score", leadId) == "99") problems.Add("Lead: the caller set its score to 99.");
            var created = DateTime.Parse(await Db.ValueAsync("Leads", "CreatedAtUtc", leadId) ?? "2001-01-01",
                System.Globalization.CultureInfo.InvariantCulture);
            if (created.Year < 2026) problems.Add($"Lead: the caller set when it was created ({created:yyyy-MM-dd}).");
        });

        // ── Lead: edit ─────────────────────────────────────────────
        await Step("An edit of the lead that also sends a score, a status and \"converted\" changes only the fields a person edits", async () =>
        {
            var scoreBefore = await Db.ValueAsync("Leads", "Score", leadId);
            var d = (await admin.GetAsync($"/api/leads/{leadId}")).Json();
            var r = await admin.PutAsync($"/api/leads/{leadId}", new
            {
                tenantId, leadId, fullName = "Mass Edited " + m, email = Str(d, "email"), phone = Str(d, "phone"),
                companyName = Str(d, "companyName"), estimatedValue = 2000m, currency = "INR",
                ownerUserId = Str(d, "ownerUserId"), version = Str(d, "version"),
                score = 99, status = "Converted", isConverted = true, isDeleted = true,
            });
            Assert.That(r.IsSuccess, Is.True, $"Editing the lead failed: {r}");
            Expect(problems, "Lead name after the edit", await Db.ValueAsync("Leads", "FullName", leadId), "Mass Edited " + m);
            Expect(problems, "Lead score after the edit", await Db.ValueAsync("Leads", "Score", leadId), scoreBefore);
            Expect(problems, "Lead status after the edit", await Db.ValueAsync("Leads", "Status", leadId), controlStatus);
            Expect(problems, "Lead converted after the edit", await Db.ValueAsync("Leads", "IsConverted", leadId), "False");
            Expect(problems, "Lead deleted after the edit", await Db.ValueAsync("Leads", "IsDeleted", leadId), "False");
        });

        // ── Deal ───────────────────────────────────────────────────
        var contactId = await Step("A contact for the deals", async () =>
        {
            var r = await admin.PostAsync("/api/contacts", new { tenantId, firstName = "Mass", lastName = "Contact " + m, email = World.EmailFor("A", "masscontact" + m) });
            Assert.That(r.IsSuccess, Is.True, $"Adding the contact failed: {r}");
            return ApiIds.IdOf(r);
        });

        var stages = (await admin.GetAsync("/api/pipeline-stages")).Json();
        var wonKey = stages.EnumerateArray()
            .Where(s => string.Equals(Str(s, "category"), "Won", StringComparison.OrdinalIgnoreCase))
            .Select(s => Str(s, "key")).FirstOrDefault();

        await Step("A deal sent straight into Won is refused, and nothing is made", async () =>
        {
            Assert.That(wonKey, Is.Not.Null, "Workspace A has no Won stage to try.");
            var title = "Mass Won " + m;
            var r = await admin.PostAsync("/api/deals", Deal(tenantId, contactId, title, wonKey!));
            if (r.IsSuccess) problems.Add($"Deal: created straight into Won ({r}).");
            else Assert.That(r.Status, Is.LessThan(500), $"Refused with a server error: {r}");
            Expect(problems, "Deals made in Won", (await Db.CountContainingAsync("Deals", tenantId, title)).ToString(), "0");
        });

        var dealId = await Step("A deal sent with someone else named as its author is written as the caller's", async () =>
        {
            var body = Deal(tenantId, contactId, "Mass Deal " + m, null);
            body["createdBy"] = "Mallory";
            body["actualValue"] = 999999m;
            var r = await admin.PostAsync("/api/deals", body);
            Assert.That(r.IsSuccess, Is.True, $"Adding the deal failed: {r}");
            var id = ApiIds.IdOf(r);
            if (await Db.ValueAsync("Deals", "CreatedBy", id) is "Mallory") problems.Add("Deal: the caller chose its author.");
            if (await Db.ValueAsync("Deals", "ActualValue", id) is { Length: > 0 } v && v.StartsWith("999999", StringComparison.Ordinal))
                problems.Add("Deal: the caller set its actual (won) value.");
            return id;
        });

        // ── Quote ──────────────────────────────────────────────────
        await Step("A quote sent as Accepted, with its own number and a total of 1, is a Draft with the server's number and its lines' total", async () =>
        {
            var r = await admin.PostAsync("/api/quotes", new
            {
                dealId, issueDateUtc = DateTime.UtcNow.Date, expiresAtUtc = DateTime.UtcNow.Date.AddDays(30), currency = "INR",
                items = new[] { new { name = "Mass line", unitPrice = 5000m, quantity = 2m, taxRate = 0m } },
                status = "Accepted", number = "QUO-HACK-" + m, grandTotal = 1m, publicLinkToken = "chosen-" + m,
            });
            Assert.That(r.IsSuccess, Is.True, $"Adding the quote failed: {r}");
            var id = ApiIds.IdOf(r);
            Expect(problems, "Quote status (0 = Draft)", await Db.ValueAsync("Quotes", "Status", id), "0");
            if ((await Db.ValueAsync("Quotes", "Number", id) ?? "").Contains("HACK")) problems.Add("Quote: the caller chose its number.");
            if (decimal.Parse(await Db.ValueAsync("Quotes", "GrandTotal", id) ?? "0", System.Globalization.CultureInfo.InvariantCulture) != 10000m)
                problems.Add("Quote: its total is not its lines' total.");
        });

        // ── Invoice ────────────────────────────────────────────────
        await Step("An invoice sent as Paid, with its own number and a zero balance, is a Draft with the server's number, owing its total", async () =>
        {
            var r = await admin.PostAsync("/api/invoices", new
            {
                dealId, issueDateUtc = DateTime.UtcNow.Date, dueDateUtc = DateTime.UtcNow.Date.AddDays(14), currency = "INR",
                lines = new[] { new { name = "Mass line", unitPrice = 3000m, quantity = 1m, taxRate = 0m } },
                status = "Paid", number = "INV-HACK-" + m, balance = 0m, amountPaid = 3000m, sendImmediately = false,
            });
            Assert.That(r.IsSuccess, Is.True, $"Adding the invoice failed: {r}");
            var id = ApiIds.IdOf(r);
            Expect(problems, "Invoice status (0 = Draft)", await Db.ValueAsync("Invoices", "Status", id), "0");
            if ((await Db.ValueAsync("Invoices", "Number", id) ?? "").Contains("HACK")) problems.Add("Invoice: the caller chose its number.");
            if (decimal.Parse(await Db.ValueAsync("Invoices", "Balance", id) ?? "0", System.Globalization.CultureInfo.InvariantCulture) != 3000m)
                problems.Add("Invoice: it does not owe its total.");
        });

        // ── Person ─────────────────────────────────────────────────
        await Step("A person added as \"super admin\", with a password hash and a security stamp of the caller's choosing, is none of those", async () =>
        {
            var email = World.EmailFor("A", "massperson" + m);
            var r = await admin.PostAsync("/api/users", new
            {
                tenantId, firstName = "Mass", lastName = "Person", email, isTenantAdmin = false, roleIds = Array.Empty<Guid>(),
                isSuperAdmin = true, mustChangePassword = true, securityStamp = "chosen", passwordHash = "chosen", isActive = true,
            });
            Assert.That(r.IsSuccess, Is.True, $"Adding the person failed: {r}");
            var id = await Db.UserIdAsync(email);
            try
            {
                Expect(problems, "Person super admin", await Db.ValueAsync("Users", "IsSuperAdmin", id), "False");
                Expect(problems, "Person must-change-password", await Db.ValueAsync("Users", "MustChangePassword", id), "False");
                var identity = await Db.ApiIdentityAsync(email);
                if (identity.SecurityStamp == "chosen") problems.Add("Person: the caller chose the security stamp.");
            }
            finally
            {
                await admin.DeleteAsync($"/api/users/{id}?tenantId={tenantId}");
            }
        });

        Assert.That(problems, Is.Empty, $"{problems.Count} protected propert(ies) could be set:\n  • " + string.Join("\n  • ", problems));
    }

    // ── helpers ──────────────────────────────────────────────────────

    private static object Lead(Guid tenantId, string name, string m) => new
    {
        tenantId, fullName = name, email = World.EmailFor("A", "lead" + m), phone = "+9198" + m + "333",
        companyName = name + " Co", estimatedValue = 1000m, currency = "INR",
    };

    /// <summary>A deal body; with no stage, the workspace's starting stage.</summary>
    private static System.Text.Json.Nodes.JsonObject Deal(Guid tenantId, Guid contactId, string title, string? stage)
    {
        var body = new System.Text.Json.Nodes.JsonObject
        {
            ["tenantId"] = tenantId.ToString(),
            ["contactId"] = contactId.ToString(),
            ["title"] = title,
            ["expectedValue"] = 10000m,
            ["currency"] = "INR",
            ["expectedCloseDateUtc"] = DateTime.UtcNow.Date.AddDays(30),
        };
        if (stage is not null) body["stage"] = stage;
        return body;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static void Expect(List<string> problems, string what, string? actual, string? expected)
    {
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            problems.Add($"{what}: expected \"{expected}\", stored \"{actual}\".");
    }
}
