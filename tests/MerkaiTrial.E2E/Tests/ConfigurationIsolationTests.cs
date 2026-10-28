// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/ConfigurationIsolationTests.cs
//
// VUL-05 (round 104) — IDOR / BOLA across every entity.
//
//   A_workspace_cannot_reach_another_workspaces_people_settings_or_messages_by_id
//
// The tenant-isolation suite (test 76, TenantIsolationSuiteTests) covers
// the business records: leads, contacts, companies, deals, quotes,
// invoices, tasks, notes, attachments, products and activities. This test
// covers EVERYTHING ELSE that has an id in an address:
//
//   people          read, edit, switch off, delete, give roles, bulk switch
//   roles           read, its people, edit, delete
//   lead statuses   edit, make default, move its leads away, delete
//   pipeline stages edit, make default, move its deals away, delete
//   custom fields   edit, delete
//   product categories  edit, remove
//   automation rules    read, its log, edit, switch on, delete
//   visibility teams    edit, delete, put A's person in B's team, managers
//   notifications   mark read, dismiss
//   sent emails / the outbox   read, send again, cancel
//   quote approvals and payment schedules   read, submit, approve, ask
//                   for changes, recall, rewrite the schedule
//   the audit log and the people pickers — searched for A's names
//
// Workspace B's ADMIN tries each one with workspace A's id (an admin, so
// only the workspace boundary can explain a refusal). A finding is:
//   • any write answered 2xx (except a bulk call, which answers "0 done",
//     and a notification's read / dismiss, which quietly do nothing for
//     one that isn't yours — the database check covers those);
//   • anything of A's — its people's emails, the names this test gave A's
//     records — in any answer;
//   • ANY change to A's rows, read from the database before and after.
// A 5xx is listed as worth a look, not as a leak.
// =====================================================================

using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Security")]
public class ConfigurationIsolationTests : E2ETest
{
    private sealed record Attempt(string What, HttpMethod Method, string Path, object? Body, bool MaySucceed);

    [Test]
    public async Task A_workspace_cannot_reach_another_workspaces_people_settings_or_messages_by_id()
    {
        var tenantA = await Db.TenantIdAsync(World.A.Name);
        var tenantB = await Db.TenantIdAsync(World.B.Name);
        var apiA = await Api.AsAsync(World.A.Admin);
        var apiB = await Api.AsAsync(World.B.Admin);
        await Api.EnsureTokensWorkAsync(apiA);
        await Api.EnsureTokensWorkAsync(apiB);
        var m = Random.Shared.Next(10000, 99999).ToString();
        var marker = "IsoA" + m;
        var hijack = "Hijacked by B " + m;

        var manager = World.A["manager"];
        var managerId = await Db.UserIdAsync(manager.Email);
        var markers = new List<string> { marker, manager.Email, World.A.Admin.Email };

        // ── A's records to aim at ───────────────────────────────────
        Guid categoryId = default, teamId = default, fieldId = default, ruleId = default;
        await Step("Workspace A has a team, a product category, a custom field and an automation rule of its own", async () =>
        {
            var team = await apiA.PostAsync("/api/record-visibility/teams", new { tenantId = tenantA, name = marker + " Team" });
            Assert.That(team.IsSuccess, Is.True, $"Creating A's team failed: {team}");
            teamId = ApiIds.IdOf(team);

            var category = await apiA.PostAsync("/api/product-categories", new { name = marker + " Category" });
            Assert.That(category.IsSuccess, Is.True, $"Creating A's category failed: {category}");
            categoryId = ApiIds.IdOf(category);

            var field = await apiA.PostAsync("/api/custom-fields", new
            {
                entityType = "Lead", label = marker + " Field", fieldType = "text", isRequired = false, isActive = false, showInList = false,
            });
            Assert.That(field.IsSuccess, Is.True, $"Creating A's custom field failed: {field}");
            fieldId = ApiIds.IdOf(field);

            var rule = await apiA.PostAsync("/api/automation/rules", RuleBody(marker + " Rule", isActive: false));
            Assert.That(rule.IsSuccess, Is.True, $"Creating A's automation rule failed: {rule}");
            ruleId = ApiIds.IdOf(rule);
        });

        try
        {
            var roleId = await Db.FirstIdAsync("Roles", tenantA) ?? throw new AssertionException("Workspace A has no role of its own.");
            var statusId = await Db.FirstIdAsync("LeadStatusDefinitions", tenantA) ?? throw new AssertionException("Workspace A has no lead status.");
            var stageId = await Db.FirstIdAsync("PipelineStages", tenantA) ?? throw new AssertionException("Workspace A has no pipeline stage.");
            var bStatusId = await Db.FirstIdAsync("LeadStatusDefinitions", tenantB) ?? Guid.NewGuid();
            var bStageId = await Db.FirstIdAsync("PipelineStages", tenantB) ?? Guid.NewGuid();
            var bRoleId = await Db.FirstIdAsync("Roles", tenantB) ?? Guid.NewGuid();
            var quoteId = await Db.FirstIdAsync("Quotes", tenantA);
            var notificationId = await Db.FirstIdAsync("Notifications", tenantA);
            var outboundId = await Db.FirstIdAsync("OutboundMessages", tenantA);

            var bTeam = await apiB.PostAsync("/api/record-visibility/teams", new { tenantId = tenantB, name = "B Team " + m });
            var bTeamId = bTeam.IsSuccess ? ApiIds.IdOf(bTeam) : Guid.NewGuid();

            // ── what B tries ────────────────────────────────────────
            var reads = new List<(string What, string Path)>
            {
                ("A's person", $"/api/users/{managerId}"),
                ("A's person's roles", $"/api/users/{managerId}/roles"),
                ("A's role", $"/api/roles/{roleId}"),
                ("A's role's people", $"/api/roles/{roleId}/users"),
                ("A's automation rule", $"/api/automation/rules/{ruleId}"),
                ("A's automation rule's log", $"/api/automation/runs?ruleId={ruleId}"),
                ("B's people picker", "/api/users/lookup"),
                ("B's sales team", "/api/users/sales-team"),
                ("B's role picker", "/api/roles/lookup"),
                ("B's teams", "/api/record-visibility/teams"),
                ("B's custom fields", "/api/custom-fields?entityType=Lead&includeInactive=true"),
                ("B's product categories", "/api/product-categories"),
                ("B's automation rules", "/api/automation/rules"),
                ("B's audit log, searched for A's names", $"/api/audit?search={Uri.EscapeDataString(marker)}"),
                ("B's audit log, searched for A's person", $"/api/audit?search={Uri.EscapeDataString(manager.Email)}"),
            };

            var writes = new List<Attempt>
            {
                new("Edit A's person", HttpMethod.Put, $"/api/users/{managerId}", new
                {
                    tenantId = tenantA, userId = managerId, firstName = hijack, lastName = "B", email = manager.Email,
                    isActive = true, isTenantAdmin = true,
                }, false),
                new("Switch A's person off", HttpMethod.Patch, $"/api/users/{managerId}/status", new { tenantId = tenantA, userId = managerId, isActive = false }, false),
                new("Give A's person B's role", HttpMethod.Post, $"/api/users/{managerId}/roles", new { tenantId = tenantA, userId = managerId, roleIds = new[] { bRoleId } }, false),
                new("Switch A's person off in bulk", HttpMethod.Post, "/api/users/bulk/status", new { tenantId = tenantA, userIds = new[] { managerId }, isActive = false }, true),
                new("Delete A's person", HttpMethod.Delete, $"/api/users/{managerId}?tenantId={tenantA}", null, false),

                new("Edit A's role", HttpMethod.Put, $"/api/roles/{roleId}", new
                {
                    roleId, name = "hijacked", displayName = hijack, description = hijack,
                    permissions = "{\"users\":[\"read\",\"create\",\"update\",\"delete\"]}",
                }, false),
                new("Delete A's role", HttpMethod.Delete, $"/api/roles/{roleId}", null, false),

                new("Edit A's lead status", HttpMethod.Put, $"/api/lead-statuses/{statusId}", new { tenantId = tenantA, statusId, name = hijack, score = 1, isActive = false }, false),
                new("Make A's lead status B's default", HttpMethod.Post, $"/api/lead-statuses/{statusId}/default", null, false),
                new("Move A's status's leads", HttpMethod.Post, $"/api/lead-statuses/{statusId}/move-leads", new { tenantId = tenantA, fromStatusId = statusId, toStatusId = bStatusId, thenRetire = true }, false),
                new("Delete A's lead status", HttpMethod.Delete, $"/api/lead-statuses/{statusId}", null, false),

                new("Edit A's pipeline stage", HttpMethod.Put, $"/api/pipeline-stages/{stageId}", new { tenantId = tenantA, stageId, name = hijack, probability = 1, isActive = false }, false),
                new("Make A's stage B's default", HttpMethod.Post, $"/api/pipeline-stages/{stageId}/default", null, false),
                new("Move A's stage's deals", HttpMethod.Post, $"/api/pipeline-stages/{stageId}/move-deals", new { tenantId = tenantA, fromStageId = stageId, toStageId = bStageId, thenRetire = true }, false),
                new("Delete A's pipeline stage", HttpMethod.Delete, $"/api/pipeline-stages/{stageId}", null, false),

                new("Edit A's custom field", HttpMethod.Put, $"/api/custom-fields/{fieldId}", new { entityType = "Lead", label = hijack, fieldType = "text", isActive = true }, false),
                new("Delete A's custom field", HttpMethod.Delete, $"/api/custom-fields/{fieldId}", null, false),

                new("Edit A's product category", HttpMethod.Put, $"/api/product-categories/{categoryId}", new { name = hijack }, false),
                new("Remove A's product category", HttpMethod.Post, $"/api/product-categories/{categoryId}/remove", new { }, false),

                new("Edit A's automation rule", HttpMethod.Put, $"/api/automation/rules/{ruleId}", RuleBody(hijack, isActive: true), false),
                new("Switch A's automation rule on", HttpMethod.Post, $"/api/automation/rules/{ruleId}/active", new { isActive = true }, false),
                new("Delete A's automation rule", HttpMethod.Delete, $"/api/automation/rules/{ruleId}", null, false),

                new("Rename A's team", HttpMethod.Put, $"/api/record-visibility/teams/{teamId}", new { tenantId = tenantA, teamId, name = hijack }, false),
                new("Delete A's team", HttpMethod.Delete, $"/api/record-visibility/teams/{teamId}", null, false),
                new("Put A's person in B's team", HttpMethod.Put, "/api/record-visibility/teams/members", new { tenantId = tenantA, userId = managerId, teamId = bTeamId }, false),
                new("Make A's person a manager of A's team", HttpMethod.Put, "/api/record-visibility/teams/managers", new { tenantId = tenantA, userId = managerId, teamIds = new[] { teamId } }, false),
            };

            if (notificationId is Guid n)
            {
                // By design these answer "done" for a notification that isn't
                // yours (a stale bell item should do nothing quietly), so the
                // database is what proves nothing happened.
                writes.Add(new("Mark A's notification read", HttpMethod.Post, $"/api/notifications/{n}/read", null, true));
                writes.Add(new("Dismiss A's notification", HttpMethod.Post, $"/api/notifications/{n}/dismiss", null, true));
            }

            if (outboundId is Guid o)
            {
                reads.Add(("A's sent email", $"/api/sent-emails/{o}"));
                writes.Add(new("Send A's email again", HttpMethod.Post, $"/api/sent-emails/{o}/retry", null, false));
                writes.Add(new("Retry A's outbox message", HttpMethod.Post, $"/api/notification-settings/outbound/{o}/retry", null, false));
                writes.Add(new("Cancel A's outbox message", HttpMethod.Post, $"/api/notification-settings/outbound/{o}/cancel", null, false));
            }

            if (quoteId is Guid q)
            {
                reads.Add(("A's quote's approval", $"/api/quote-approvals/quotes/{q}"));
                reads.Add(("A's quote's payment schedule", $"/api/quotes/{q}/billing-schedule"));
                foreach (var act in new[] { "submit", "approve", "request-changes", "recall" })
                    writes.Add(new($"Quote approval: {act} A's quote", HttpMethod.Post, $"/api/quote-approvals/quotes/{q}/{act}", new { comment = hijack }, false));
                writes.Add(new("Rewrite A's quote's payment schedule", HttpMethod.Put, $"/api/quotes/{q}/billing-schedule",
                    new { milestones = new[] { new { sortOrder = 10, name = hijack, percent = (decimal?)100m } } }, false));
            }

            // ── A's rows, before ────────────────────────────────────
            var rows = new List<(string Table, Guid Id)>
            {
                ("Users", managerId), ("Roles", roleId), ("LeadStatusDefinitions", statusId), ("PipelineStages", stageId),
                ("CustomFieldDefinitions", fieldId), ("ProductCategories", categoryId), ("AutomationRules", ruleId), ("Teams", teamId),
            };
            if (notificationId is Guid nn) rows.Add(("Notifications", nn));
            if (quoteId is Guid qq) rows.Add(("Quotes", qq));
            var counts = new List<(string Table, string Column, Guid Id)> { ("UserRoles", "UserId", managerId) };
            if (quoteId is Guid qm) counts.Add(("QuoteMilestones", "QuoteId", qm));

            var before = await SnapshotAsync(rows, counts);
            var leaks = new List<string>();
            var warnings = new List<string>();

            await Step($"{reads.Count} reads as workspace B", async () =>
            {
                foreach (var (what, path) in reads)
                {
                    var r = await apiB.GetAsync(path);
                    var found = markers.Where(x => r.Body.Contains(x, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (found.Count > 0) leaks.Add($"{what}: GET {path} showed workspace A's data ({string.Join(", ", found)}).");
                    if (r.Status >= 500) warnings.Add($"{what}: GET {path} → HTTP {r.Status}.");
                }
            });

            await Step($"{writes.Count} writes as workspace B", async () =>
            {
                foreach (var w in writes)
                {
                    var r = await apiB.SendAsync(w.Method, w.Path, w.Body);
                    if (r.IsSuccess && !w.MaySucceed)
                        leaks.Add($"{w.What}: {w.Method.Method} {w.Path} was ACCEPTED ({r}). It must be refused — it is workspace A's.");
                    var found = markers.Where(x => r.Body.Contains(x, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (found.Count > 0) leaks.Add($"{w.What}: the answer showed workspace A's data ({string.Join(", ", found)}).");
                    if (r.Status >= 500) warnings.Add($"{w.What}: {w.Method.Method} {w.Path} → HTTP {r.Status}.");
                }
            });

            await Step("Workspace A's rows are exactly as they were", async () =>
            {
                var after = await SnapshotAsync(rows, counts);
                foreach (var (key, was) in before)
                    if (after[key] != was)
                        leaks.Add($"Workspace A's data CHANGED: {key} — was {ApiResponse.Short(was, 300)}, now {ApiResponse.Short(after[key], 300)}.");
            });

            if (warnings.Count > 0)
                TestContext.Progress.WriteLine("  Refused, but with a server error instead of a plain 404 (worth a look, not a leak):\n    " +
                                               string.Join("\n    ", warnings));

            Assert.That(leaks, Is.Empty, $"Workspace B reached workspace A ({leaks.Count} problem(s)):\n  • " + string.Join("\n  • ", leaks));
        }
        finally
        {
            await apiA.DeleteAsync($"/api/custom-fields/{fieldId}");
            await apiA.PostAsync($"/api/product-categories/{categoryId}/remove", new { });
            await apiA.DeleteAsync($"/api/record-visibility/teams/{teamId}");
            await Db.RetireAutomationRulesAsync(tenantA, marker);
        }
    }

    private static object RuleBody(string name, bool isActive) => new
    {
        name,
        entityType = "Lead",
        trigger = "RecordCreated",
        conditions = new[] { new { field = "company", op = "contains", value = "no-such-company-" + name } },
        actions = new object[] { new { kind = "task", subject = "Follow up {name}", dueInDays = 1, assignTo = "owner" } },
        isActive,
    };

    private static async Task<Dictionary<string, string>> SnapshotAsync(
        List<(string Table, Guid Id)> rows, List<(string Table, string Column, Guid Id)> counts)
    {
        var snap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (table, id) in rows) snap[$"rows {table} {id}"] = await Db.RowJsonAsync(table, id);
        foreach (var (table, column, id) in counts)
            snap[$"count of {table} rows with {column} = {id}"] = (await Db.CountWhereAsync(table, column, id)).ToString();
        return snap;
    }
}
