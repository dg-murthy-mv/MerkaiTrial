// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/WorkspaceSettingsTests.cs
//
// NEW (088). What a workspace admin configures really changes what the
// team sees and can do. All in workspace C — Thailand / THB /
// Asia/Bangkok — built before the first test here, so changing the sales
// process or the approval rules cannot leak into the other tests.
//
//   1. Company profile → printed on the customer's quote (and money in
//      baht: ฿267,500.00 with 7% VAT).
//   2. Sales process → "Closed Won" needs an accepted quote: the button is
//      shown disabled with the reason, posting the move anyway is refused
//      by the server, and once the customer accepts, the move works.
//   3. Quote approvals → a rep's quote over the discount limit waits for
//      the manager of the rep's team (Record Visibility → Teams), cannot
//      be approved by the rep, and can be sent once approved. The request
//      time is checked against BANGKOK time.
//   4. Renamed lead status and stage → the new names appear on the lead,
//      the conversion page, the deal and the board, and conversion still
//      works. The old names are put back afterwards.
//      088c: the new rule must be listed as ON — the first runs found
//      every rule saved from that page was switched off and ignored.
//      088d: the approval box selectors follow the live panel
//      (_QuoteApprovalPanel.cshtml, 033: .qap / .qap.pending / .qap.ok,
//      #qapDecide, #qapComment) — the test had been written against the
//      older 017 markup and could not see the box. 088e: Approve is
//      clicked as #qapDecide's own button, not by exact button name.
//   5. Record visibility "Own" → a rep cannot find or open a colleague's
//      lead — and setting it in C does not change workspace A, even though
//      the built-in roles are shared between workspaces.
//
// Each test sets up what it needs itself, so they can run in any order or
// one at a time (-Filter "FullyQualifiedName~WorkspaceSettings").
// =====================================================================

using System.Globalization;
using System.Text.RegularExpressions;
using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("Settings")]
public class WorkspaceSettingsTests : E2ETest
{
    private static TestTenant C => World.C ?? throw new InvalidOperationException("Workspace C was not built.");

    [OneTimeSetUp]
    public async Task BuildWorkspaceCAsync()
    {
        if (World.C is null)
            await new WorldBuilder(GlobalSetup.Browser, GlobalSetup.Db).BuildSettingsWorkspaceAsync();
    }

    // =================================================================
    // 1. Company profile
    // =================================================================

    [Test]
    public async Task The_company_profile_is_printed_on_the_customers_quote()
    {
        var legalName = $"Siam Fit-out Co., Ltd. {World.Stamp}";
        const string address = "99 Sukhumvit Road";
        const string taxId = "0105561234567";
        var admin = await SignedInAsync(C.Admin);

        await Step("Fill in Settings → Company Profile; the preview follows the typing", async () =>
        {
            await admin.GotoAsync("/Settings/CompanyProfile");
            await admin.Locator("#Input_LegalName").FillAsync(legalName);
            await admin.Locator("#Input_AddressLine1").FillAsync(address);
            await admin.Locator("#Input_City").FillAsync("Bangkok");
            await admin.Locator("#Input_PostalCode").FillAsync("10110");
            await admin.Locator("#Input_TaxNumberLabel").FillAsync("Tax ID");
            await admin.Locator("#Input_TaxNumber").FillAsync(taxId);
            await admin.Locator("#Input_Phone").FillAsync("+6621234567");
            await admin.Locator("#Input_ReplyToEmail").FillAsync(World.EmailFor("C", "sales"));

            await Assertions.Expect(admin.Locator("#cpName")).ToContainTextAsync(legalName);
            await Assertions.Expect(admin.Locator("#cpAddr")).ToContainTextAsync(address);
            await Assertions.Expect(admin.Locator("#cpTax")).ToBeVisibleAsync();
            await Assertions.Expect(admin.Locator("#cpTax")).ToContainTextAsync(taxId);
        });

        await Step("Save, and the details are still there after a reload", async () =>
        {
            await admin.GetByRole(AriaRole.Button, new() { Name = "Save company details" }).ClickAsync();
            await Ui.ExpectSuccessAsync(admin, "Company details saved");

            await admin.GotoAsync("/Settings/CompanyProfile");
            Assert.That(await admin.Locator("#Input_LegalName").InputValueAsync(), Is.EqualTo(legalName));
            Assert.That(await admin.Locator("#Input_TaxNumber").InputValueAsync(), Is.EqualTo(taxId));
            await Assertions.Expect(admin.GetByText("Your quotes don't say who they're from yet")).ToHaveCountAsync(0);
        });

        var dealTitle = await Step("Add, qualify and convert a lead", () => NewDealAsync(admin, "Somchai Profile"));

        // 2 × ฿125,000 = ฿250,000; 7% VAT = ฿17,500; total ฿267,500.
        await Step("Send a quote: 2 × 125,000 at 7% VAT", async () =>
        {
            await Flows.FillQuoteAsync(admin, dealTitle, new QuoteLine("Showroom fit-out", 2, 125000m, TaxRate: 7));
            await Flows.SubmitQuoteAsync(admin);
        });

        await Step("The quote page shows baht with Western grouping", async () =>
        {
            var text = await admin.Locator("main").First.InnerTextAsync();
            Assert.That(text, Does.Contain("฿250,000.00"), "Subtotal in baht.");
            Assert.That(text, Does.Contain("฿17,500.00"), "VAT at 7%.");
            Assert.That(text, Does.Contain("฿267,500.00"), "Total in baht.");
            Assert.That(text, Does.Not.Contain("2,67,500"), "Baht printed with Indian (lakh) grouping.");
        });

        var link = await Step("The quote has a customer link", () => Flows.CustomerLinkAsync(admin));

        await Step("The customer's page says who the quote is from, with the tax ID", async () =>
        {
            var customer = await NewPageAsync("customer");
            await customer.GotoAsync(link);

            var seller = customer.Locator(".seller");
            try
            {
                await Assertions.Expect(seller).ToBeVisibleAsync();
            }
            catch (Exception ex) when (ex is not AssertionException)
            {
                throw new AssertionException(
                    "The customer's quote page has no \"Quote from\" block, although the company profile was saved before the quote was sent.");
            }

            await Assertions.Expect(seller).ToContainTextAsync(legalName);
            await Assertions.Expect(seller).ToContainTextAsync(address);
            await Assertions.Expect(seller).ToContainTextAsync(taxId);

            var body = await customer.Locator("body").InnerTextAsync();
            Assert.That(body, Does.Contain("฿267,500.00"), "Total on the customer's page.");
            Assert.That(body, Does.Contain("THB"), "The customer's page does not say THB.");
        });
    }

    // =================================================================
    // 2. Sales process
    // =================================================================

    [Test]
    public async Task Closed_won_waits_for_an_accepted_quote()
    {
        var admin = await SignedInAsync(C.Admin);
        var dealTitle = await Step("Add, qualify and convert a lead", () => NewDealAsync(admin, "Niran Process"));
        var dealId = await Db.DealIdByTitleAsync(await Db.TenantIdAsync(C.Name), dealTitle);
        var dealUrl = $"/Pipeline/Detail/{dealId}";

        var current = await Step("Read the deal's stage", async () =>
        {
            await admin.GotoAsync(dealUrl);
            return (await admin.Locator(".transition-bar strong").First.InnerTextAsync()).Trim();
        });

        var won = await Step($"Settings → Sales Process: \"{current} → Won\" needs an accepted quote", async () =>
        {
            await admin.GotoAsync("/Settings/PipelineRules");
            var cell = await FindWonCellAsync(admin, current);

            await admin.Locator($".cell-btn[data-idx='{cell.Index}']").ClickAsync();
            await Assertions.Expect(admin.Locator("#stepPanel")).ToBeVisibleAsync();
            await admin.Locator("#pActive").CheckAsync();
            await admin.Locator("#pAq").CheckAsync();
            await admin.Locator("#pApply").ClickAsync();
            await Assertions.Expect(admin.Locator("#stepPanel")).Not.ToBeVisibleAsync();

            await admin.GetByRole(AriaRole.Button, new() { Name = "Save process" }).ClickAsync();
            await Ui.ExpectSuccessAsync(admin, "Sales process saved");
            return cell;
        });

        await Step("The rule is still there after a reload", async () =>
        {
            await admin.GotoAsync("/Settings/PipelineRules");
            var cell = await FindWonCellAsync(admin, current);
            Assert.That(cell.IsActive, Is.True, "The step was switched off again by the save.");
            Assert.That(cell.RequiresAcceptedQuote, Is.True, "\"An accepted quote\" did not stick.");
        });

        await Step("On the deal, the Won move is shown disabled, with the reason", async () =>
        {
            await admin.GotoAsync(dealUrl);
            await Assertions.Expect(admin.Locator($".move-btn[data-stage='{won.ToKey}']")).ToHaveCountAsync(0);

            var blocked = admin.Locator(".transition-bar .blocked-move", new() { HasText = won.Label });
            if (await blocked.CountAsync() == 0)
                throw new AssertionException(
                    $"No disabled \"{won.Label}\" button. The bar says: " +
                    await Ui.TextOrEmptyAsync(admin.Locator(".transition-bar")));

            var why = (await blocked.First.Locator(".blocked-why").InnerTextAsync()).Trim();
            Assert.That(why.ToLowerInvariant(), Does.Contain("quote"),
                $"The reason under the disabled button doesn't mention a quote: \"{why}\".");
        });

        await Step("Posting the move anyway is refused by the server", async () =>
        {
            // What a hand-made request would do: the hidden form the page
            // uses for moves that need no note, aimed at Won.
            await admin.EvaluateAsync<string>(
                @"key => { document.getElementById('directMoveStage').value = key;
                           document.getElementById('directMoveForm').submit();
                           return 'posted'; }", won.ToKey);

            await Assertions.Expect(admin.Locator(".alert-danger").First)
                .ToBeVisibleAsync(new() { Timeout = E2ESettings.ActionTimeoutMs });
            await Assertions.Expect(StageBadge(admin)).ToContainTextAsync(current);
        });

        await Step("Send a quote and accept it as the customer", async () =>
        {
            await Flows.FillQuoteAsync(admin, dealTitle, new QuoteLine("Design package", 1, 80000m));
            await Flows.SubmitQuoteAsync(admin);
            var link = await Flows.CustomerLinkAsync(admin);
            await Flows.AcceptAsCustomerAsync(await NewPageAsync("customer"), link);
        });

        await Step("Now the Won move works", async () =>
        {
            await admin.GotoAsync(dealUrl);
            var move = admin.Locator($".move-btn[data-stage='{won.ToKey}']");
            if (await move.CountAsync() == 0)
                throw new AssertionException(
                    $"\"{won.Label}\" is still not available after the quote was accepted. The bar says: " +
                    await Ui.TextOrEmptyAsync(admin.Locator(".transition-bar")));

            await move.ClickAsync();

            // A step can ask for a note first; answer it if it does.
            var modal = admin.Locator("#moveNoteModal");
            try
            {
                await modal.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 2_500 });
                await admin.Locator("#moveNoteText").FillAsync("Signed after the customer accepted the quote (e2e).");
                await modal.Locator("button[type=submit]").ClickAsync();
            }
            catch (Exception ex) when (Ui.IsTimeout(ex))
            {
                // No note asked for — the move posted straight away.
            }

            await Assertions.Expect(StageBadge(admin))
                .ToContainTextAsync(won.ToName, new() { Timeout = E2ESettings.ActionTimeoutMs });
            await Assertions.Expect(admin.GetByText("Won — closed")).ToBeVisibleAsync();
        });
    }

    // =================================================================
    // 3. Quote approvals (+ time zone)
    // =================================================================

    [Test]
    public async Task A_reps_discounted_quote_waits_for_the_team_manager()
    {
        var rep = C["rep"];
        var manager = C["manager"];
        var team = $"C Sales {World.Stamp}";
        var ruleName = $"Discount over 10% {World.Stamp}";

        var admin = await SignedInAsync(C.Admin);

        await Step("Record Visibility: a team the rep belongs to and the manager manages", async () =>
        {
            await EnsureTeamAsync(admin, team);
            await SavePersonAsync(admin, rep, panel => Ui.SelectByTextAsync(panel.Locator("select[name=teamId]"), team));
            await SavePersonAsync(admin, manager, panel =>
                panel.Locator(".rv-mbox .form-check", new() { HasText = team }).Locator("input[name=teamIds]").CheckAsync());
        });

        await Step("Quote Approvals: switched on, with a rule for discounts over 10%", async () =>
        {
            await admin.GotoAsync("/Settings/QuoteApprovals");

            var switchOn = admin.GetByRole(AriaRole.Button, new() { Name = "Switch approvals on" });
            if (await switchOn.CountAsync() > 0)
            {
                await switchOn.ClickAsync();
                await Ui.ExpectSuccessAsync(admin, "Quote approvals are on");
            }

            if (await admin.Locator($".qa-rule[data-name='{ruleName}']").CountAsync() == 0)
            {
                await admin.Locator("#addRule").EvaluateAsync<bool>("el => el.open = true");
                var form = admin.Locator("#addRule form");
                await form.Locator("input[name=RuleName]").FillAsync(ruleName);
                await form.Locator("input[name=UseDiscount]").CheckAsync();
                await form.Locator("input[name=DiscountOverPercent]").FillAsync("10");
                // Step 1 of a new rule is already "managers of the deal owner's team".
                await form.GetByRole(AriaRole.Button, new() { Name = "Add this rule" }).ClickAsync();
                await Ui.ExpectSuccessAsync(admin, $"Rule \"{ruleName}\" added");
            }

            // 088c: "Rule is on" was ticked (it is, by default, on a new
            // rule), so the rule must be listed as on. An off rule is
            // ignored, and the rep's quote would go out without sign-off.
            var rule = admin.Locator($".qa-rule[data-name='{ruleName}']");
            await Assertions.Expect(rule).ToHaveCountAsync(1);
            Assert.That(await rule.GetAttributeAsync("data-active"), Is.EqualTo("1"),
                "The new rule was saved switched OFF although \"Rule is on\" was ticked, so it will be ignored. " +
                "_RuleEditor.cshtml must post the checkbox before its hidden \"false\" (round 088c).");
        });

        var repPage = await SignedInAsync(rep);
        var dealTitle = await Step("The rep adds, qualifies and converts their own lead", () => NewDealAsync(repPage, "Kittisak Discount"));

        await Step("The rep's 15% discount: the editor warns approval will be needed", async () =>
        {
            await Flows.FillQuoteAsync(repPage, dealTitle,
                new QuoteLine("Workstations", 2, 100000m, TaxRate: 7, DiscountPercent: 15));
            await Assertions.Expect(repPage.Locator("#qeHint")).ToBeVisibleAsync();
            await Assertions.Expect(repPage.Locator("#qcSend")).ToContainTextAsync("submit for approval");
        });

        var submittedUtc = DateTime.UtcNow;
        await Step("Save & submit: it goes for approval, not to the customer", async () =>
        {
            var said = await Flows.SubmitQuoteAsync(repPage);

            // 088b: when there is no "Waiting for approval" box, say what the
            // page DID say — the green message tells sent / submitted /
            // left as draft apart, and the approval box says why.
            try
            {
                await Assertions.Expect(repPage.Locator(".qap.pending"))
                    .ToBeVisibleAsync(new() { Timeout = E2ESettings.ActionTimeoutMs });
            }
            catch (Exception ex) when (ex is not AssertionException)
            {
                var panels = await repPage.Locator(".qap").AllInnerTextsAsync();
                var status = await Ui.TextOrEmptyAsync(repPage.Locator("main .badge"));
                throw new AssertionException(
                    "The quote is not waiting for approval. " +
                    $"After saving, the page said: \"{(said.Length > 0 ? said : "(no green message)")}\". " +
                    $"Status badge: \"{status}\". " +
                    (panels.Count == 0
                        ? "There is no approval box on the quote at all. "
                        : "Approval box: " + string.Join(" || ", panels.Select(p => System.Text.RegularExpressions.Regex.Replace(p, @"\s+", " ").Trim())) + ". ") +
                    await Ui.DescribePageProblemsAsync(repPage));
            }
            await Assertions.Expect(repPage.Locator(".qap.pending")).ToContainTextAsync("Waiting for approval");
            await Assertions.Expect(repPage.Locator("#qdLink")).ToHaveCountAsync(0);
        });
        var answeredUtc = DateTime.UtcNow;
        var quoteUrl = repPage.Url;

        await Step("It goes to the team's manager, and the rep cannot approve it", async () =>
        {
            await Assertions.Expect(repPage.Locator(".qap.pending")).ToContainTextAsync(manager.FullName);
            await Assertions.Expect(repPage.Locator("#qapDecide")).ToHaveCountAsync(0);
        });

        await Step("The request time is shown in Bangkok time (UTC+7)", async () =>
        {
            var meta = await repPage.Locator(".qap.pending .qap-meta").First.InnerTextAsync();
            ExpectShownInWorkspaceTime(meta, submittedUtc, answeredUtc, C.UtcOffset, "the approval request");
        });

        await Step("The manager approves it", async () =>
        {
            var mgr = await SignedInAsync(manager);
            await mgr.GotoAsync(quoteUrl);
            await mgr.Locator("#qapComment").FillAsync("OK at 15% for a two-year commitment.");
            // 088e: the Approve button inside the decision form, found by its
            // own markup. An exact accessible-name match ("Approve") never
            // matched: the Bootstrap icon in the button adds its font glyph
            // to the name Chromium computes.
            await mgr.Locator("#qapDecide button.btn-success").ClickAsync();
            await Assertions.Expect(mgr.Locator(".qap.ok"))
                .ToBeVisibleAsync(new() { Timeout = E2ESettings.ActionTimeoutMs });
        });

        await Step("The rep can now send it, and the customer link appears", async () =>
        {
            await repPage.GotoAsync(quoteUrl);
            await Assertions.Expect(repPage.Locator(".qap.ok")).ToContainTextAsync("ready to send");

            var send = repPage.Locator("form.qd-move:has(input[name=newStatus][value='Sent']) button");
            if (await send.CountAsync() == 0)
            {
                var offered = await repPage.Locator("form.qd-move button").AllInnerTextsAsync();
                throw new AssertionException(
                    "The approved quote offers no way to send it. Buttons: " +
                    (offered.Count == 0 ? "(none)" : string.Join(" | ", offered.Select(t => t.Trim()))));
            }

            await send.First.ClickAsync();   // a confirm(), if any, is accepted by the session
            await Flows.CustomerLinkAsync(repPage);
        });
    }

    // =================================================================
    // 4. Renamed lead status and stage
    // =================================================================

    [Test]
    public async Task Renamed_status_and_stage_are_used_everywhere()
    {
        var suffix = Random.Shared.Next(100, 999).ToString(CultureInfo.InvariantCulture);
        var newStatus = $"Ready to buy {suffix}";
        var newStage = $"Site survey {suffix}";
        var admin = await SignedInAsync(C.Admin);

        // Read before renaming, so the finally block can always put back
        // whatever was actually changed.
        string? statusId = null, oldStatus = null, stageId = null, oldStage = null;

        try
        {
            await Step("Lead Statuses: rename the Qualified status", async () =>
            {
                await admin.GotoAsync("/Settings/LeadStatuses");
                var row = admin.Locator(".lf-status[data-cat='1']:not(.lf-status-retired)").First;
                var id = await row.GetAttributeAsync("data-id") ?? throw new AssertionException("No Qualified status on Lead Statuses.");
                var old = await row.GetAttributeAsync("data-name") ?? "";
                (statusId, oldStatus) = (id, old);
                await RenameRowAsync(admin, "/Settings/LeadStatuses", id, newStatus, ".lf-status");
            });

            await Step("Pipeline Stages: rename the stage new deals start in", async () =>
            {
                await admin.GotoAsync("/Settings/Pipeline");
                var row = admin.Locator(".lf-stage[data-cat='0']:not(.lf-stage-retired):has(.badge.bg-primary)").First;
                var id = await row.GetAttributeAsync("data-id") ?? throw new AssertionException("No starting stage on Pipeline Stages.");
                var old = await row.GetAttributeAsync("data-name") ?? "";
                (stageId, oldStage) = (id, old);
                await RenameRowAsync(admin, "/Settings/Pipeline", id, newStage, ".lf-stage");
            });

            var leadName = World.Unique("Pim Rename");
            var dealTitle = World.Unique("Clinic renovation");

            await Step("A new lead offers the new status name, not the old one", async () =>
            {
                await Flows.CreateLeadAsync(admin, leadName, World.EmailFor("C", "pim" + suffix), 120000m, C);
                var options = await Ui.OptionTextsAsync(admin.Locator("#statusChangeForm select[name=status]"));
                Assert.That(options.Any(o => o.Contains(newStatus)), Is.True,
                    $"The status list has no \"{newStatus}\": {string.Join(" | ", options)}");
                Assert.That(options.Any(o => o.Trim() == oldStatus!), Is.False,
                    $"The status list still offers the old name \"{oldStatus}\".");
            });

            await Step("Choosing it qualifies the lead (Convert appears)", () =>
                Flows.QualifyLeadAsync(admin, Regex.Escape(newStatus)));

            await Step("The conversion page offers the renamed stage, and converting works", async () =>
            {
                await admin.Locator("a[href*='ConvertToDeal']").First.ClickAsync();
                await Assertions.Expect(admin.Locator("label.lc-stage", new() { HasText = newStage }).First).ToBeVisibleAsync();
                await admin.GoBackAsync();
                await Flows.ConvertLeadAsync(admin, dealTitle, 120000m, Regex.Escape(newStage));
            });

            await Step("The deal and the board show the new stage name", async () =>
            {
                var dealId = await Db.DealIdByTitleAsync(await Db.TenantIdAsync(C.Name), dealTitle);
                await admin.GotoAsync($"/Pipeline/Detail/{dealId}");
                await Assertions.Expect(StageBadge(admin)).ToContainTextAsync(newStage);
                await Assertions.Expect(admin.Locator(".transition-bar strong").First).ToContainTextAsync(newStage);

                await admin.GotoAsync("/Pipeline");
                await Assertions.Expect(admin.Locator("main").First).ToContainTextAsync(newStage);
            });
        }
        finally
        {
            // Put the names back so the other tests here see the defaults.
            // Best effort: a failure is logged, not thrown over the real one.
            await RestoreNameAsync(admin, "/Settings/LeadStatuses", statusId, oldStatus, ".lf-status");
            await RestoreNameAsync(admin, "/Settings/Pipeline", stageId, oldStage, ".lf-stage");
        }
    }

    // =================================================================
    // 5. Record visibility
    // =================================================================

    [Test]
    public async Task Own_records_visibility_hides_a_colleagues_lead()
    {
        var repRole = C.RoleOf["rep"];
        var aRepRole = World.A.RoleOf.TryGetValue("rep", out var r) ? r : repRole;

        var aAdmin = await SignedInAsync(World.A.Admin);
        var aBefore = await Step("Note what workspace A's reps see today", async () =>
        {
            await aAdmin.GotoAsync("/Settings/Visibility");
            return (await ReadScopeCellAsync(aAdmin, aRepRole, "Leads")).Current;
        });

        var admin = await SignedInAsync(C.Admin);
        var original = await Step($"Workspace C: set \"{repRole}\" to Own for leads", async () =>
        {
            await admin.GotoAsync("/Settings/Visibility");
            var cell = await ReadScopeCellAsync(admin, repRole, "Leads");
            await SetScopeAsync(admin, cell, "Own");
            return cell.Current;
        });

        try
        {
            var hiddenLead = World.Unique("Ploy Private");
            var leadId = await Step("Rep 2 adds a lead and can open it", async () =>
            {
                var rep2 = await SignedInAsync(C["rep2"]);
                return await Flows.CreateLeadAsync(rep2, hiddenLead, World.EmailFor("C", "ploy" + Random.Shared.Next(1000, 9999)), 60000m, C);
            });

            var rep = await SignedInAsync(C["rep"]);

            await Step("The rep cannot find it in the leads list", async () =>
            {
                await rep.GotoAsync("/Leads?SearchTerm=" + Uri.EscapeDataString(hiddenLead));
                await Assertions.Expect(rep.Locator(".lx-table a", new() { HasText = hiddenLead })).ToHaveCountAsync(0);
            });

            await Step("…and its address does not open for them", async () =>
            {
                // "Not found" or a redirect — either way, nothing of the
                // lead is on the page (the same check as tenant isolation).
                await rep.GotoAsync($"/Leads/Detail/{leadId}");
                await Assertions.Expect(rep.GetByText(hiddenLead)).ToHaveCountAsync(0);
            });

            await Step("Workspace A's reps are unchanged", async () =>
            {
                await aAdmin.GotoAsync("/Settings/Visibility");
                var after = (await ReadScopeCellAsync(aAdmin, aRepRole, "Leads")).Current;
                Assert.That(after, Is.EqualTo(aBefore),
                    $"Changing \"{repRole}\" in workspace C changed it in workspace A too ({aBefore} → {after}). " +
                    "Built-in roles are shared between workspaces, so their record visibility must be stored per workspace.");
            });
        }
        finally
        {
            try
            {
                await admin.GotoAsync("/Settings/Visibility");
                await SetScopeAsync(admin, await ReadScopeCellAsync(admin, repRole, "Leads"), original);
            }
            catch (Exception ex)
            {
                TestContext.Progress.WriteLine($"[e2e] Could not put {repRole} back to {original}: {ex.Message}");
            }
        }
    }

    // =================================================================
    // Helpers
    // =================================================================

    /// <summary>
    /// A lead in workspace C, qualified and converted into a stage quotes
    /// can be raised from. Returns the deal's title; the page ends on the
    /// pipeline board.
    /// </summary>
    private static async Task<string> NewDealAsync(IPage page, string person)
    {
        var leadName = World.Unique(person);
        var dealTitle = World.Unique(person.Split(' ')[0] + " project");
        await Flows.CreateLeadAsync(page, leadName, World.EmailFor("C", "c" + Random.Shared.Next(10000, 99999)), 200000m, C);
        await Flows.QualifyLeadAsync(page);
        await Flows.ConvertLeadAsync(page, dealTitle, 200000m);
        return dealTitle;
    }

    /// <summary>The stage badge in a deal's header.</summary>
    private static ILocator StageBadge(IPage page) => page.Locator("main span.badge.fs-6").First;

    private sealed record WonCell(int Index, string ToKey, string ToName, string Label, bool IsActive, bool RequiresAcceptedQuote);

    /// <summary>
    /// On Settings → Sales Process: the grid cell for "from this stage to
    /// the (first) Won stage", read from the hidden fields the page posts.
    /// </summary>
    private static async Task<WonCell> FindWonCellAsync(IPage page, string fromStageName)
    {
        var found = await page.EvaluateAsync<string[]?>(
            @"from => {
                const box = document.getElementById('cellFields');
                if (!box) return null;
                for (const d of box.querySelectorAll('[data-cell]')) {
                    const i = d.getAttribute('data-cell');
                    const el = n => d.querySelector('[name=""Cells[' + i + '].' + n + '""]');
                    const v = n => { const e = el(n); return e ? e.value : ''; };
                    const c = n => { const e = d.querySelector('input[type=checkbox][name=""Cells[' + i + '].' + n + '""]'); return e && e.checked ? '1' : '0'; };
                    const cat = v('ToCategory');
                    if (v('FromStageName').trim() === from && (cat === 'Won' || cat === '1'))
                        return [i, v('ToStageKey'), v('ToStageName'), v('Label'), c('IsActive'), c('RequiresAcceptedQuote')];
                }
                return null;
            }", fromStageName);

        if (found is null)
            throw new AssertionException(
                $"Settings → Sales Process has no step from \"{fromStageName}\" to a Won stage. " +
                await Ui.DescribePageProblemsAsync(page));

        return new WonCell(int.Parse(found[0], CultureInfo.InvariantCulture), found[1], found[2], found[3],
            found[4] == "1", found[5] == "1");
    }

    /// <summary>Opens a row's edit form through ?EditId= (the path that
    /// needs no script), types the new name and saves.</summary>
    private static async Task RenameRowAsync(IPage page, string listPath, string id, string newName, string rowSelector)
    {
        await page.GotoAsync($"{listPath}?EditId={id}");
        var edit = page.Locator($"#lf-edit-{id}");
        await Assertions.Expect(edit).ToBeVisibleAsync();
        await edit.Locator("input[name='Input.Name']").FillAsync(newName);
        await edit.Locator("button[type=submit]").ClickAsync();

        var renamed = page.Locator($"{rowSelector}[data-id='{id}']");
        try
        {
            await Assertions.Expect(renamed).ToHaveAttributeAsync("data-name", newName,
                new() { Timeout = E2ESettings.ActionTimeoutMs });
        }
        catch (Exception ex) when (ex is not AssertionException)
        {
            throw new AssertionException($"Renaming to \"{newName}\" did not stick. " + await Ui.DescribePageProblemsAsync(page));
        }
    }

    private static async Task RestoreNameAsync(IPage page, string listPath, string? id, string? oldName, string rowSelector)
    {
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(oldName)) return;
        try
        {
            await RenameRowAsync(page, listPath, id, oldName, rowSelector);
        }
        catch (Exception ex)
        {
            TestContext.Progress.WriteLine($"[e2e] Could not rename {listPath} row back to \"{oldName}\": {ex.Message}");
        }
    }

    // ── Record Visibility page ────────────────────────────────────────

    /// <summary>Creates the team unless it is already listed.</summary>
    private static async Task EnsureTeamAsync(IPage page, string team)
    {
        await page.GotoAsync("/Settings/Visibility");
        if (await page.Locator(".rv-team .rv-teamname", new() { HasText = team }).CountAsync() > 0) return;

        // A native <details>: opened by setting the attribute, because
        // clicking its summary would CLOSE it when it starts open (it does,
        // in a workspace with no teams yet).
        await page.Locator("#rvAddTeam").EvaluateAsync<bool>("el => el.open = true");
        await page.Locator("#rvAddTeam input[name=name]").FillAsync(team);
        await page.Locator("#rvAddTeam").GetByRole(AriaRole.Button, new() { Name = "Add team" }).ClickAsync();
        await Ui.ExpectSuccessAsync(page, $"Team \"{team}\" added");
    }

    /// <summary>Opens one person's panel on Record Visibility, lets
    /// <paramref name="change"/> set it up, and presses "Save this person".</summary>
    private static async Task SavePersonAsync(IPage page, TestUser person, Func<ILocator, Task> change)
    {
        await page.GotoAsync("/Settings/Visibility");
        var panel = page.Locator("details.rv-person", new() { Has = page.Locator(".rv-pmeta", new() { HasText = person.Email }) });
        if (await panel.CountAsync() == 0)
            throw new AssertionException($"Record Visibility lists nobody with the email {person.Email}.");

        await panel.First.EvaluateAsync<bool>("el => el.open = true");
        await change(panel.First);
        await panel.First.GetByRole(AriaRole.Button, new() { Name = "Save this person" }).ClickAsync();
        await Ui.ExpectSuccessAsync(page, "Saved");
    }

    private sealed record ScopeCell(string Current, Dictionary<string, string> RadioIds);

    /// <summary>
    /// The "Who sees whose records" cell for a role and a module: which of
    /// Own / Team / All is ticked, and the id of each radio.
    /// </summary>
    private static async Task<ScopeCell> ReadScopeCellAsync(IPage page, string roleName, string module)
    {
        var raw = await page.EvaluateAsync<string[]>(
            @"args => {
                const [role, module] = args;
                const heads = [...document.querySelectorAll('table.rv-grid thead th')].map(t => t.textContent.trim());
                let col = heads.findIndex(t => t.toLowerCase() === module.toLowerCase());
                if (col < 1) col = heads.findIndex((t, i) => i > 0 && t.toLowerCase().includes(module.toLowerCase().replace(/s$/, '')));
                if (col < 1) return ['error', 'no ""' + module + '"" column — columns are: ' + heads.join(' | ')];
                const rows = [...document.querySelectorAll('tr.rv-rolerow')];
                const nameOf = r => (r.querySelector('.rv-rolename')?.textContent || '').trim();
                // Exact first. Otherwise the longest role name contained in
                // the label Settings → Users showed, which can carry a
                // description after the name.
                let row = rows.find(r => nameOf(r) === role);
                if (!row) row = rows.filter(r => nameOf(r) && role.includes(nameOf(r)))
                                    .sort((a, b) => nameOf(b).length - nameOf(a).length)[0];
                if (!row) return ['error', 'no row for the role ""' + role + '"" — roles are: ' +
                                  rows.map(r => (r.querySelector('.rv-rolename')?.textContent || '').trim()).join(' | ')];
                const cell = row.children[col];
                if (!cell || !cell.classList.contains('rv-cell')) return ['error', '""' + role + '"" is locked (it always sees everything)'];
                const out = ['ok', ''];
                for (const r of cell.querySelectorAll('input.btn-check')) {
                    if (r.checked) out[1] = r.value;
                    out.push(r.value + '=' + r.id);
                }
                return out;
            }", new[] { roleName, module });

        if (raw.Length < 2 || raw[0] != "ok")
            throw new AssertionException("Record Visibility: " + (raw.Length > 1 ? raw[1] : "the grid could not be read."));

        var ids = raw.Skip(2)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => p[1], StringComparer.OrdinalIgnoreCase);
        return new ScopeCell(raw[1], ids);
    }

    /// <summary>Ticks Own / Team / All in a cell and saves the grid.</summary>
    private static async Task SetScopeAsync(IPage page, ScopeCell cell, string scope)
    {
        if (string.Equals(cell.Current, scope, StringComparison.OrdinalIgnoreCase)) return;
        if (!cell.RadioIds.TryGetValue(scope, out var id))
            throw new AssertionException($"Record Visibility offers no \"{scope}\" here (offers: {string.Join(", ", cell.RadioIds.Keys)}).");

        await page.Locator($"label[for='{id}']").ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Save visibility" }).ClickAsync();
        await Ui.ExpectSuccessAsync(page, "Saved");
    }

    // ── Time zone ─────────────────────────────────────────────────────

    /// <summary>
    /// Finds a date-time in <paramref name="text"/> and checks it is the
    /// moment the action happened, in the WORKSPACE's time. On failure says
    /// whether it matched UTC or India time instead — the two likely
    /// mistakes (no conversion, or the server computer's own zone).
    /// </summary>
    private static void ExpectShownInWorkspaceTime(string text, DateTime fromUtc, DateTime toUtc, TimeSpan offset, string what)
    {
        var shown = Ui.FindDateTimes(text);
        if (shown.Count == 0)
            throw new AssertionException($"No date and time found for {what} in: \"{text.Trim()}\"");

        // Minutes are printed without seconds, so allow the minute either
        // side plus a little clock drift between the browser and the server.
        var slack = TimeSpan.FromMinutes(3);
        bool Within(DateTime s, TimeSpan off) =>
            s >= (fromUtc + off - slack) && s <= (toUtc + off + slack);

        if (shown.Any(s => Within(s, offset))) return;

        var printed = string.Join(", ", shown.Select(s => s.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)).Distinct());
        var expected = (fromUtc + offset).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
        var diagnosis =
            shown.Any(s => Within(s, TimeSpan.Zero)) ? " That is UTC — the time was not converted to the workspace's zone."
            : shown.Any(s => Within(s, TimeSpan.FromHours(5.5))) ? " That is India time — the server's own clock zone, not the workspace's."
            : shown.Any(s => Within(s, TimeZoneInfo.Local.GetUtcOffset(fromUtc))) ? " That is this computer's local time, not the workspace's."
            : "";

        Assert.Fail($"The time shown for {what} was {printed}; in the workspace's time (UTC{(offset >= TimeSpan.Zero ? "+" : "-")}{offset:hh\\:mm}) it should be about {expected}.{diagnosis}");
    }
}
