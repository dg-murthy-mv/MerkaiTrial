// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/PlanFeatureTests.cs
//
// NEW (114). The plan's features are enforced — on the pages and in the
// API — and so is its storage limit.
//
// Workspace F (114-update1: was "P", the performance tests' key) is moved to the STARTER plan (leads, deals, contacts,
// companies, tasks, notes, basic_reports). Its admin — who has every
// permission — is still refused what the plan does not include:
//
//   API → 403 "Not in your plan":
//     custom fields (add, reorder)        custom_fields
//     pipeline stages, lead statuses,     deal_pipeline_custom
//       the sales process (change)
//     a new role                          custom_roles
//     the Activity Log                    audit_logs
//     the inbox and its settings          email_integration
//     Deal Velocity, Stalled Deals,       advanced_reports
//       Revenue Forecast
//   …while what Starter includes still answers: the Pipeline report, the
//   stage list, the custom field list, the role list.
//
//   Pages → "Not in your plan" (/Upgrade, naming the feature and the plans
//   that include it) for the same things; the Roles page opens (editing
//   roles is in every plan) with its New role button locked; the menu shows
//   a lock on Custom Fields.
//
//   Storage: with the limit set below what is already stored, an upload is
//   refused with "storage is full" and nothing is saved; with the plan's
//   limit back, the same upload goes through.
//
// Workspace A (the biggest plan) gets the same reads answered normally, and
// its Custom Fields and Activity Log pages open, so the refusals come from
// the plan and not from anything else.
//
// Every wrong answer is collected and reported together.
// =====================================================================

using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Entitlements")]
public class PlanFeatureTests : E2ETest
{
    private const string NotInPlan = "Not in your plan";

    [Test]
    public async Task Plan_features_and_storage_are_enforced_on_pages_and_api()
    {
        var p = await Step("Workspace F (India, admin only) on the Starter plan — built the first time it is needed",
            WorldBuilder.EnsureWorkspaceFAsync);
        var tenantP = await Db.TenantIdAsync(p.Name);
        var apiP = await Api.AsAsync(p.Admin);
        apiP.WaitWhenPaced = true;
        var apiA = await Api.AsAsync(World.A.Admin);
        apiA.WaitWhenPaced = true;

        var period = $"from={DateTime.UtcNow.AddMonths(-3):yyyy-MM-dd}&to={DateTime.UtcNow:yyyy-MM-dd}";
        var problems = new List<string>();

        // (method, path, body, feature)
        var refused = new (HttpMethod Method, string Path, object? Body, string Feature)[]
        {
            (HttpMethod.Post, "/api/custom-fields", new { }, "custom_fields"),
            (HttpMethod.Put,  "/api/custom-fields/order", new { }, "custom_fields"),
            (HttpMethod.Post, "/api/pipeline-stages", new { }, "deal_pipeline_custom"),
            (HttpMethod.Post, "/api/lead-statuses", new { }, "deal_pipeline_custom"),
            (HttpMethod.Put,  "/api/pipeline-rules", new { }, "deal_pipeline_custom"),
            (HttpMethod.Post, "/api/roles", new { }, "custom_roles"),
            (HttpMethod.Get,  "/api/audit", null, "audit_logs"),
            (HttpMethod.Get,  "/api/inbound-emails", null, "email_integration"),
            (HttpMethod.Get,  "/api/inbound-emails/settings", null, "email_integration"),
            (HttpMethod.Get,  $"/api/reports/deal-velocity?{period}", null, "advanced_reports"),
            (HttpMethod.Get,  "/api/reports/stalled-deals", null, "advanced_reports"),
            (HttpMethod.Get,  "/api/reports/revenue-forecast", null, "advanced_reports"),
        };

        await Step("Starter, through the API: every Professional / Enterprise feature answers 403 \"Not in your plan\"", async () =>
        {
            foreach (var (method, path, body, feature) in refused)
            {
                var r = await apiP.SendAsync(method, path, body);
                if (r.Status != 403 || !r.Body.Contains(NotInPlan, StringComparison.OrdinalIgnoreCase))
                    problems.Add($"starter: {method} {path} answered {r} (expected 403 \"{NotInPlan}\" for {feature})");
                else if (!r.Body.Contains(feature, StringComparison.OrdinalIgnoreCase))
                    problems.Add($"starter: {method} {path} refused, but doesn't name the feature {feature}: {r}");
            }
        });

        await Step("Starter, through the API: what Starter includes still answers", async () =>
        {
            foreach (var path in new[]
                     {
                         $"/api/reports/pipeline-summary?{period}",
                         "/api/pipeline-stages",
                         "/api/custom-fields?entityType=Contact",
                         "/api/roles",
                     })
            {
                var r = await apiP.GetAsync(path);
                if (!r.IsSuccess)
                    problems.Add($"starter: GET {path} answered {r} (expected 200 — it's in every plan)");
            }
        });

        // Reads only: the writes above, sent with empty bodies, must never
        // reach a workspace whose sales process other tests rely on.
        await Step("The biggest plan, through the API: the same reads answer 200", async () =>
        {
            foreach (var (method, path, _, feature) in refused.Where(x => x.Method == HttpMethod.Get))
            {
                var r = await apiA.GetAsync(path);
                if (!r.IsSuccess)
                    problems.Add($"workspace A: GET {path} answered {r} (expected 200 — {feature} is in its plan)");
            }
        });

        // ── pages ────────────────────────────────────────────────────
        var page = await SignedInAsync(p.Admin);
        await Step("Starter, on the pages: each one says \"Not in your plan\" and names the feature", async () =>
        {
            foreach (var (path, feature) in new[]
                     {
                         ("/Settings/CustomFields", "custom_fields"),
                         ("/Settings/Pipeline", "deal_pipeline_custom"),
                         ("/Settings/PipelineRules", "deal_pipeline_custom"),
                         ("/Settings/LeadStatuses", "deal_pipeline_custom"),
                         ("/Settings/Roles/Create", "custom_roles"),
                         ("/Settings/AuditLogs", "audit_logs"),
                         ("/Inbox", "email_integration"),
                         ("/Settings/InboundEmail", "email_integration"),
                         ("/Reports/StalledDeals", "advanced_reports"),
                     })
            {
                await page.GotoAsync(path);
                var landed = Ui.PathOf(page.Url);
                if (!landed.StartsWith("/Upgrade", StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add($"starter: {path} opened {landed} (expected the Not in your plan page)");
                    continue;
                }
                var shown = await page.Locator("#upgradeFeature").GetAttributeAsync("data-feature");
                if (!string.Equals(shown, feature, StringComparison.OrdinalIgnoreCase))
                    problems.Add($"starter: {path} → Not in your plan for \"{shown}\" (expected {feature})");
            }

            await page.GotoAsync("/Upgrade?feature=custom_fields");
            if (await page.Locator("#upgradePlans").CountAsync() == 0 ||
                !(await page.Locator("#upgradePlans").InnerTextAsync()).Contains("Professional", StringComparison.OrdinalIgnoreCase))
                problems.Add("the Not in your plan page for Custom Fields doesn't list Professional");
        });

        await Step("Starter, on the pages: Roles opens with New role locked; the Pipeline report opens; the menu shows the lock", async () =>
        {
            await page.GotoAsync("/Settings/Roles");
            if (!Ui.PathOf(page.Url).StartsWith("/Settings/Roles", StringComparison.OrdinalIgnoreCase))
                problems.Add($"starter: /Settings/Roles did not open (landed on {Ui.PathOf(page.Url)}) — editing roles is in every plan");
            else if (await page.Locator("#newRoleButton").GetAttributeAsync("data-locked") != "true")
                problems.Add("starter: the New role button is not shown as locked");

            await page.GotoAsync("/Reports/Pipeline");
            if (!Ui.PathOf(page.Url).StartsWith("/Reports/Pipeline", StringComparison.OrdinalIgnoreCase))
                problems.Add($"starter: the Pipeline report did not open (landed on {Ui.PathOf(page.Url)}) — Starter has basic reports");

            await page.GotoAsync("/Dashboard");
            if (await page.Locator("a.lf-nav-item[href='/Settings/CustomFields'] .lf-lock-icon").CountAsync() != 1)
                problems.Add("starter: the menu's Custom Fields link has no lock");
        });

        await Step("The biggest plan, on the pages: Custom Fields and the Activity Log open", async () =>
        {
            var a = await SignedInAsync(World.A.Admin);
            foreach (var path in new[] { "/Settings/CustomFields", "/Settings/AuditLogs" })
            {
                await a.GotoAsync(path);
                if (!Ui.PathOf(a.Url).StartsWith(path, StringComparison.OrdinalIgnoreCase))
                    problems.Add($"workspace A: {path} did not open (landed on {Ui.PathOf(a.Url)})");
            }
        });

        // ── storage ──────────────────────────────────────────────────
        await Step("Storage: below the limit an upload is refused (\"storage is full\"); with the plan's limit it goes through", async () =>
        {
            var m = Random.Shared.Next(10000, 99999).ToString();
            var contact = await ApiRecords.ContactAsync(apiP, tenantP, $"Storage {m}", "st" + m);
            var deal = await ApiRecords.DealAsync(apiP, tenantP, contact, $"Storage deal {m}");
            var planLimit = await PlanData.StorageLimitAsync(tenantP);

            try
            {
                await PlanData.SetStorageLimitAsync(tenantP, 1);
                var full = await apiP.UploadAsync($"/api/deals/{deal}/attachments", $"full-{m}.pdf",
                    Files.SmallPdf("Over the limit " + m), "application/pdf");
                if (full.Status != 400 || !full.Body.Contains("storage is full", StringComparison.OrdinalIgnoreCase))
                    problems.Add($"storage: an upload over the limit answered {full} (expected 400 \"storage is full\")");
                if (await Db.AttachmentAsync(deal, $"full-{m}.pdf") is not null)
                    problems.Add("storage: the refused file was saved anyway");
            }
            finally
            {
                await PlanData.SetStorageLimitAsync(tenantP, planLimit);
            }

            var ok = await apiP.UploadAsync($"/api/deals/{deal}/attachments", $"fits-{m}.pdf",
                Files.SmallPdf("Within the limit " + m), "application/pdf");
            if (!ok.IsSuccess)
                problems.Add($"storage: with the plan's limit the upload answered {ok} (expected 200)");
        });

        if (problems.Count > 0)
            Assert.Fail($"{problems.Count} plan problem(s):\n  • " + string.Join("\n  • ", problems));
    }
}
