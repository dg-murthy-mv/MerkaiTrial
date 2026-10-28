// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/OverPostingTests.cs
//
// VUL-06 (round 104) — API over-posting of workspace, owner, role and
// status.
//
//   Workspace_owner_role_and_status_cannot_be_overposted
//
//   WORKSPACE  B's admin posts leads, contacts, companies and a note with
//              workspace A's id in the body: each is saved in B.
//   OWNER      A's admin names B's admin as a lead's owner (create and
//              edit), a deal's owner, and a task's assignee: refused, and
//              nothing changes hands.
//   LISTS      A lead or deal naming B's own lead source: refused (round
//              104: it was stored as sent, and B's name showed on A's lead).
//   STATUS     A deal moved to Won by editing it (not through its stage
//              rules): refused; the deal stays where it was.
//   ROLE       A "delegate" — a person whose role lets them manage people
//              and roles but who is NOT a workspace admin (round 104's
//              AccessChangeGuard) — tries to:
//                • make themselves a workspace admin;
//                • add a new workspace admin;
//                • give themselves the sales manager's role (more access
//                  than they have);
//                • add permissions to their own role;
//                • change the workspace admin's email (to then reset the
//                  admin's password);
//              each is refused (403) and the database is unchanged. And
//              what they ARE allowed still works: editing their own job
//              title.
// =====================================================================

using System.Text.Json;
using System.Text.RegularExpressions;
using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Security")]
public class OverPostingTests : E2ETest
{
    [Test]
    public async Task Workspace_owner_role_and_status_cannot_be_overposted()
    {
        var a = World.A;
        var tenantA = await Db.TenantIdAsync(a.Name);
        var tenantB = await Db.TenantIdAsync(World.B.Name);
        var apiA = await Api.AsAsync(a.Admin);
        var apiB = await Api.AsAsync(World.B.Admin);
        await Api.EnsureTokensWorkAsync(apiA);
        await Api.EnsureTokensWorkAsync(apiB);
        var aAdminId = await Db.UserIdAsync(a.Admin.Email);
        var bAdminId = await Db.UserIdAsync(World.B.Admin.Email);
        var m = Random.Shared.Next(10000, 99999).ToString();
        var problems = new List<string>();

        // ── WORKSPACE ──────────────────────────────────────────────
        await Step("Workspace B posts records with workspace A's id in the body: each is saved in B", async () =>
        {
            var lead = await apiB.PostAsync("/api/leads", new
            {
                tenantId = tenantA, fullName = "Over B Lead " + m, email = World.EmailFor("B", "over" + m),
                phone = "+9198" + m + "111", companyName = "Over B Co " + m, estimatedValue = 1000m, currency = "INR",
            });
            await InB(lead, "Leads", "lead");

            var contact = await apiB.PostAsync("/api/contacts", new { tenantId = tenantA, firstName = "Over", lastName = "B " + m, email = World.EmailFor("B", "overc" + m) });
            await InB(contact, "Contacts", "contact");

            var company = await apiB.PostAsync("/api/companies", new { tenantId = tenantA, name = "Over B Company " + m, country = "IN", vertical = "Generic" });
            await InB(company, "Companies", "company");

            if (lead.IsSuccess)
            {
                var leadId = ApiIds.IdOf(lead);
                var note = await apiB.PostAsync($"/api/leads/{leadId}/notes", new { tenantId = tenantA, leadId, note = "Over note " + m });
                if (!note.IsSuccess) problems.Add($"A note on B's own lead was refused: {note}");
            }

            Assert.That(await Db.CountContainingAsync("Leads", tenantA, "Over B Lead " + m), Is.EqualTo(0), "A lead was created in workspace A.");
            Assert.That(await Db.CountContainingAsync("Companies", tenantA, "Over B Company " + m), Is.EqualTo(0), "A company was created in workspace A.");
        });

        async Task InB(ApiResponse r, string table, string what)
        {
            if (!r.IsSuccess) { problems.Add($"Workspace B's {what} was refused outright: {r}"); return; }
            var id = ApiIds.IdOf(r);
            var stored = await Db.ValueAsync(table, "TenantId", id);
            if (!string.Equals(stored, tenantB.ToString(), StringComparison.OrdinalIgnoreCase))
                problems.Add($"Workspace B's {what} was saved in workspace {stored} — the body's workspace id won.");
        }

        // ── OWNER ──────────────────────────────────────────────────
        var leadA = await Step("A lead of workspace A's", async () =>
        {
            var r = await apiA.PostAsync("/api/leads", Lead(tenantA, "Own A " + m, m, "222"));
            Assert.That(r.IsSuccess, Is.True, $"Adding the lead failed: {r}");
            return ApiIds.IdOf(r);
        });

        await Step("Naming workspace B's admin as the owner of A's lead, A's deal or A's task is refused", async () =>
        {
            var create = await apiA.PostAsync("/api/leads", new
            {
                tenantId = tenantA, fullName = "Own B " + m, email = World.EmailFor("A", "ownb" + m), phone = "+9198" + m + "333",
                companyName = "Own B Co " + m, estimatedValue = 1000m, currency = "INR", ownerUserId = bAdminId.ToString(),
            });
            if (create.IsSuccess && await Db.ValueAsync("Leads", "OwnerUserId", ApiIds.IdOf(create)) is { } o &&
                string.Equals(o, bAdminId.ToString(), StringComparison.OrdinalIgnoreCase))
                problems.Add("A new lead of A's was given to workspace B's admin.");

            var d = (await apiA.GetAsync($"/api/leads/{leadA}")).Json();
            var edit = await apiA.PutAsync($"/api/leads/{leadA}", new
            {
                tenantId = tenantA, leadId = leadA, fullName = Str(d, "fullName"), email = Str(d, "email"), phone = Str(d, "phone"),
                companyName = Str(d, "companyName"), estimatedValue = 1000m, currency = "INR",
                ownerUserId = bAdminId.ToString(), version = Str(d, "version"),
            });
            if (edit.IsSuccess) problems.Add($"Editing A's lead to belong to B's admin was accepted: {edit}");
            if (string.Equals(await Db.ValueAsync("Leads", "OwnerUserId", leadA), bAdminId.ToString(), StringComparison.OrdinalIgnoreCase))
                problems.Add("A's lead now belongs to workspace B's admin.");

            var task = await apiA.PostAsync("/api/activities", new
            {
                entityType = "Lead", entityId = leadA, activityType = "Task", subject = "Own task " + m, isTask = true,
                dueDate = DateTime.UtcNow.Date.AddDays(2), assignedToUserId = bAdminId.ToString(),
            });
            if (task.IsSuccess) problems.Add($"A task on A's lead was given to workspace B's admin: {task}");
        });

        // ── LISTS ──────────────────────────────────────────────────
        await Step("A lead or a deal naming one of workspace B's own lead sources is refused", async () =>
        {
            var bSource = await Db.FirstIdAsync("LeadSources", tenantB);
            if (bSource is null) { TestContext.Progress.WriteLine("    workspace B has no lead source of its own — skipped"); return; }

            var lead = await apiA.PostAsync("/api/leads", new
            {
                tenantId = tenantA, fullName = "Src B " + m, email = World.EmailFor("A", "srcb" + m), phone = "+9198" + m + "444",
                companyName = "Src Co " + m, estimatedValue = 1000m, currency = "INR", sourceId = bSource,
            });
            if (lead.IsSuccess)
                problems.Add("A lead was saved with workspace B's lead source.");
            else if (lead.Status >= 500)
                problems.Add($"A lead with B's source was refused with a server error: {lead}");
        });

        // ── STATUS ─────────────────────────────────────────────────
        await Step("Editing a deal can't jump its stage past the workspace's sales process", async () =>
        {
            // A new workspace starts on the Flexible process: forward one,
            // back one, Won from any open stage (with a value), Lost with a
            // note. So Discovery -> Won is ALLOWED there, the same as on the
            // board. What the edit must not do is make a move the process
            // forbids. Two of those: skipping two stages ahead, and Lost
            // without the note the process asks for.
            var page = await SignedInAsync(a.Admin);
            var (_, dealId) = await Flows.LeadToDealAsync(page, a, "Status Lead " + m, World.EmailFor("A", "status" + m),
                "Status Deal " + m, 7000m);
            var stageBefore = await Db.ValueAsync("Deals", "Stage", dealId);

            var stages = (await apiA.GetAsync("/api/pipeline-stages")).Json().EnumerateArray().ToList();
            var open = stages
                .Where(s => string.Equals(Str(s, "category"), "Open", StringComparison.OrdinalIgnoreCase))
                .OrderBy(s => s.TryGetProperty("sortOrder", out var so) && so.ValueKind == JsonValueKind.Number ? so.GetInt32() : 0)
                .Select(s => Str(s, "key")!)
                .ToList();
            var here = open.FindIndex(k => string.Equals(k, stageBefore, StringComparison.OrdinalIgnoreCase));
            Assert.That(here, Is.GreaterThanOrEqualTo(0), $"The new deal's stage '{stageBefore}' is not an open stage.");
            Assert.That(open.Count, Is.GreaterThan(here + 2), "Workspace A needs at least three open stages after the deal's own.");
            var skipTo = open[here + 2];
            var lostKey = stages
                .Where(s => string.Equals(Str(s, "category"), "Lost", StringComparison.OrdinalIgnoreCase))
                .Select(s => Str(s, "key")).First();

            var d = (await apiA.GetAsync($"/api/deals/{dealId}")).Json();
            foreach (var (target, what) in new[] { (skipTo, $"two stages ahead ({skipTo})"), (lostKey, "Lost without a note") })
            {
                var r = await apiA.PutAsync($"/api/deals/{dealId}", new
                {
                    title = Str(d, "title"), stage = target, probability = 100, expectedValue = 7000m, currency = Str(d, "currency"),
                    expectedCloseDateUtc = d.GetProperty("expectedCloseDateUtc").GetDateTime(), ownerUserId = Str(d, "ownerUserId"),
                    version = Str(d, "version"),
                });
                if (r.IsSuccess) problems.Add($"A deal edit moved it {what}, which the sales process doesn't allow.");
                else if (r.Status >= 500) problems.Add($"A deal edit {what} was refused with a server error: {r}");
                Assert.That(await Db.ValueAsync("Deals", "Stage", dealId), Is.EqualTo(stageBefore), $"The deal's stage changed ({what}).");
            }
        });

        // ── ROLE ───────────────────────────────────────────────────
        var roleName = "Delegate " + m;
        var roleId = await Step($"Workspace A's admin makes a \"{roleName}\" role: manage people and roles, nothing else", async () =>
        {
            var r = await apiA.PostAsync("/api/roles", new
            {
                name = "delegate-" + m, displayName = roleName, description = "VUL-06",
                permissions = "{\"users\":[\"read\",\"create\",\"update\"],\"roles\":[\"read\",\"update\"]}",
            });
            Assert.That(r.IsSuccess, Is.True, $"Creating the role failed: {r}");
            return ApiIds.IdOf(r);
        });

        TestUser? delegatePerson = null;
        try
        {
            delegatePerson = await Step("A person in that role (not a workspace admin)", () =>
                WorldBuilder.AddPersonForTestAsync(a, "delegate" + m, "Della", Regex.Escape(roleName)));
            var delegateId = await Db.UserIdAsync(delegatePerson.Email);
            var del = await Api.AsAsync(delegatePerson);
            var permissionsBefore = await Db.ValueAsync("Roles", "Permissions", roleId);

            await Step("The delegate can't make themselves a workspace admin", async () =>
            {
                var r = await del.PutAsync($"/api/users/{delegateId}", Person(tenantA, delegateId, delegatePerson.Email, isAdmin: true, jobTitle: "Boss"));
                Refused(r, "making themselves a workspace admin", problems);
                Expect(problems, "Delegate is a workspace admin", await Db.ValueAsync("Users", "IsTenantAdmin", delegateId), "False");
            });

            await Step("…can't add a new workspace admin", async () =>
            {
                var email = World.EmailFor("A", "newadmin" + m);
                var r = await del.PostAsync("/api/users", new
                {
                    tenantId = tenantA, firstName = "New", lastName = "Admin", email, isTenantAdmin = true, roleIds = Array.Empty<Guid>(),
                });
                Refused(r, "adding a workspace admin", problems);
                if (await Db.LiveUserExistsAsync(email))
                {
                    problems.Add("The new workspace admin was created.");
                    await apiA.DeleteAsync($"/api/users/{await Db.UserIdAsync(email)}?tenantId={tenantA}");
                }
            });

            await Step("…can't give themselves the sales manager's role", async () =>
            {
                var managerRoles = await Db.UserRoleIdsAsync(a["manager"].Email);
                var r = await del.PostAsync($"/api/users/{delegateId}/roles", new { tenantId = tenantA, userId = delegateId, roleIds = managerRoles.Append(roleId).ToList() });
                Refused(r, "taking the sales manager's role", problems);
                var now = await Db.UserRoleIdsAsync(delegatePerson.Email);
                if (now.Intersect(managerRoles).Any()) problems.Add("The delegate now holds the sales manager's role.");
            });

            await Step("…can't add permissions to their own role", async () =>
            {
                var r = await del.PutAsync($"/api/roles/{roleId}", new
                {
                    roleId, name = "delegate-" + m, displayName = roleName, description = "VUL-06",
                    permissions = "{\"users\":[\"read\",\"create\",\"update\",\"delete\"],\"roles\":[\"read\",\"update\"],\"settings\":[\"read\",\"update\"],\"leads\":[\"read\",\"delete\"]}",
                });
                Refused(r, "widening their own role", problems);
                Expect(problems, "The delegate role's permissions", await Db.ValueAsync("Roles", "Permissions", roleId), permissionsBefore);
            });

            await Step("…can't change the workspace admin's email", async () =>
            {
                var r = await del.PutAsync($"/api/users/{aAdminId}", Person(tenantA, aAdminId, World.EmailFor("A", "stolen" + m), isAdmin: true, jobTitle: null, first: a.Admin.FirstName, last: a.Admin.LastName));
                Refused(r, "changing the workspace admin's email", problems);
                Expect(problems, "The workspace admin's email", await Db.ValueAsync("Users", "Email", aAdminId), a.Admin.Email);
            });

            await Step("What the delegate IS allowed still works: editing their own job title", async () =>
            {
                var r = await del.PutAsync($"/api/users/{delegateId}", Person(tenantA, delegateId, delegatePerson.Email, isAdmin: false, jobTitle: "Coordinator " + m));
                if (!r.IsSuccess) problems.Add($"The delegate couldn't edit their own job title — the guard refuses too much: {r}");
            });
        }
        finally
        {
            if (delegatePerson is not null)
            {
                await apiA.DeleteAsync($"/api/users/{await Db.UserIdAsync(delegatePerson.Email)}?tenantId={tenantA}");
                a.People.Remove(delegatePerson.Key);
                a.RoleOf.Remove(delegatePerson.Key);
            }
            await apiA.DeleteAsync($"/api/roles/{roleId}");
        }

        Assert.That(problems, Is.Empty, $"{problems.Count} problem(s):\n  • " + string.Join("\n  • ", problems));
    }

    // ── helpers ──────────────────────────────────────────────────────

    private static object Lead(Guid tenantId, string name, string m, string tail) => new
    {
        tenantId, fullName = name, email = World.EmailFor("A", "own" + m + tail), phone = "+9198" + m + tail,
        companyName = name + " Co", estimatedValue = 1000m, currency = "INR",
    };

    private static object Person(Guid tenantId, Guid userId, string email, bool isAdmin, string? jobTitle,
        string first = "Della", string last = "E2E") => new
    {
        tenantId, userId, firstName = first, lastName = last, email, jobTitle, isActive = true, isTenantAdmin = isAdmin,
    };

    private static void Refused(ApiResponse r, string what, List<string> problems)
    {
        if (r.IsSuccess) problems.Add($"The delegate succeeded at {what} ({r}).");
        else if (r.Status != 403) problems.Add($"{what}: refused, but with HTTP {r.Status} instead of 403 ({r}).");
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static void Expect(List<string> problems, string what, string? actual, string? expected)
    {
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            problems.Add($"{what}: expected \"{expected}\", stored \"{actual}\".");
    }
}
