// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/AuditLogIntegrityTests.cs
//
// VUL-28 (round 106) — the audit log can only grow, and only the right
// people can read it.
//
//   The_audit_log_cannot_be_changed_and_is_read_only_by_its_workspace
//
//   • Control: adding a lead writes a LeadCreated row.
//   • At the database itself (agreed in 106, Sql/106_AuditLogs_AddOnly.sql):
//     changing that row, and deleting it, are both refused — by anyone,
//     not just the app. (Tried inside a transaction that is always rolled
//     back.)
//   • Through the API there is no way to change or delete audit rows at
//     all: PUT, PATCH and DELETE on the audit routes get "not found" or
//     "not allowed", and the row is as it was.
//   • Reading: workspace A's admin finds the row; workspace B's admin, and
//     A's rep (no audit permission), do not — through the API or the page.
// =====================================================================

using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Security")]
public class AuditLogIntegrityTests : E2ETest
{
    [Test]
    public async Task The_audit_log_cannot_be_changed_and_is_read_only_by_its_workspace()
    {
        var a = World.A;
        var tenantA = await Db.TenantIdAsync(a.Name);
        var apiA = await Api.AsAsync(a.Admin);
        var apiB = await Api.AsAsync(World.B.Admin);
        var apiRep = await Api.AsAsync(a["rep"]);
        var m = Random.Shared.Next(10000, 99999).ToString();
        var problems = new List<string>();
        var start = DateTime.UtcNow.AddSeconds(-2);
        var name = "Audit Lead " + m;

        var row = await Step("Control: adding a lead writes a LeadCreated row", async () =>
        {
            var leadId = await ApiRecords.LeadAsync(apiA, tenantA, name, "audit" + m);
            var rows = await Db.AuditAsync("LeadCreated", start, tenantA);
            return rows.FirstOrDefault(x => x.EntityId == leadId)
                ?? throw new AssertionException("Adding a lead wrote no LeadCreated row, so there is nothing to protect.");
        });

        await Step("At the database itself: changing the row and deleting it are both refused", async () =>
        {
            var (update, delete) = await Db.TryTamperWithAuditAsync(row.Id);
            if (update is null) problems.Add("An UPDATE of an audit row at the database went through. Was Sql/106_AuditLogs_AddOnly.sql run on MerkaiTrial_E2E?");
            else if (!update.Contains("add-only", StringComparison.OrdinalIgnoreCase)) problems.Add($"The UPDATE was refused, but not by the add-only rule: {update}");
            if (delete is null) problems.Add("A DELETE of an audit row at the database went through.");
            else if (!delete.Contains("add-only", StringComparison.OrdinalIgnoreCase)) problems.Add($"The DELETE was refused, but not by the add-only rule: {delete}");

            var still = (await Db.AuditAsync("LeadCreated", start, tenantA)).FirstOrDefault(x => x.Id == row.Id);
            if (still is null) problems.Add("The audit row is GONE.");
            else if (still.By != row.By) problems.Add($"The audit row was CHANGED ('{row.By}' → '{still.By}').");
        });

        await Step("Through the API there is no way to change or delete audit rows", async () =>
        {
            var calls = new List<(string What, Func<Task<ApiResponse>> Call)>
            {
                ($"PUT /api/audit/{row.Id}", () => apiA.PutAsync($"/api/audit/{row.Id}", new { by = "tampered", action = "Nothing" })),
                ($"PATCH /api/audit/{row.Id}", () => apiA.PatchAsync($"/api/audit/{row.Id}", new { by = "tampered" })),
                ($"DELETE /api/audit/{row.Id}", () => apiA.DeleteAsync($"/api/audit/{row.Id}")),
                ("DELETE /api/audit", () => apiA.DeleteAsync("/api/audit")),
                ("POST /api/audit (a made-up row)", () => apiA.PostAsync("/api/audit", new { action = "LeadDeleted", entityType = "Lead", entityId = row.EntityId, by = "someone else" })),
            };

            foreach (var (what, call) in calls)
            {
                var r = await call();
                if (r.IsSuccess) problems.Add($"{what} was ACCEPTED: {r}");
                else if (r.Status >= 500) problems.Add($"{what} gave a server error: {r}");
            }

            var still = (await Db.AuditAsync("LeadCreated", start, tenantA)).FirstOrDefault(x => x.Id == row.Id);
            if (still is null || still.By != row.By) problems.Add("After the API calls the audit row is gone or changed.");
            if ((await Db.AuditAsync("LeadDeleted", start, tenantA)).Any(x => x.EntityId == row.EntityId && x.By == "someone else"))
                problems.Add("A made-up audit row was ADDED through the API.");
        });

        await Step("Reading: A's admin finds the row; B's admin and A's rep do not", async () =>
        {
            var own = await apiA.GetAsync("/api/audit?pageNumber=1&pageSize=50&search=" + Uri.EscapeDataString(name));
            if (!own.IsSuccess || !own.Body.Contains(row.EntityId.ToString()!, StringComparison.OrdinalIgnoreCase) && !own.Body.Contains(name, StringComparison.Ordinal))
                problems.Add($"Workspace A's admin couldn't find the row (control): {own}");

            var b = await apiB.GetAsync("/api/audit?pageNumber=1&pageSize=50&search=" + Uri.EscapeDataString(name));
            if (b.Body.Contains(name, StringComparison.Ordinal) || b.Body.Contains(row.EntityId.ToString()!, StringComparison.OrdinalIgnoreCase))
                problems.Add("Workspace B's admin can read workspace A's audit row.");

            var rep = await apiRep.GetAsync("/api/audit?pageNumber=1&pageSize=50");
            if (rep.IsSuccess) problems.Add($"A's rep, who has no audit permission, read the audit log through the API: {rep}");

            var repPage = await SignedInAsync(a["rep"]);
            await repPage.GotoAsync("/Settings/AuditLogs");
            if (Ui.PathOf(repPage.Url).StartsWith("/Settings/AuditLogs", StringComparison.OrdinalIgnoreCase)
                && (await repPage.Locator("body").InnerTextAsync()).Contains(name, StringComparison.Ordinal))
                problems.Add("A's rep, who has no audit permission, read the audit log page.");
        });

        Assert.That(problems, Is.Empty, $"{problems.Count} problem(s):\n  • " + string.Join("\n  • ", problems));
    }
}
