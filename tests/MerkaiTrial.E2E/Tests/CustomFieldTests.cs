// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Tests/CustomFieldTests.cs
//
// NEW (089). Custom fields, end to end, in workspace D (India / INR) so a
// required field cannot get in the way of the leads the other tests add.
//
//   1. A lead's custom fields carry over when it is converted:
//        • Settings → Custom fields: a "Budget" number field on Contacts,
//          Deals and Leads, and a "Licence tier" choice on Deals and Leads;
//          the Lead fields set to copy into the Contact / Deal ones. The
//          Leads tab says where each one goes ("On conversion").
//        • The values typed on the lead's Edit page show on the lead with
//          Indian grouping (12,50,000).
//        • The leads list's "Filter by additional details" finds the lead
//          by Budget, and leaves it out when the minimum is higher.
//        • Converting says "3 additional details carried over", and the
//          deal and the new contact show the same values. The choice is
//          matched by NAME — the deal's list has a third option first.
//   2. A required lead field:
//        • a lead saved before the field existed shows it as Not provided;
//        • its Edit page will not save without it — the browser stops it,
//          and with the browser's check removed the server refuses it and
//          names the field under its own input;
//        • filled in, it saves;
//        • switched off, it leaves the form and the value stays on the lead,
//          marked Retired. (It is switched off again even if a step fails,
//          so later lead edits in workspace D are never blocked by it.)
//
// 089b: a "copy to" choice is checked to be pickable (not greyed out)
// the moment it is chosen. The first run found the Add form left every
// matching field greyed out after the type was changed from Text, so the
// mapping was silently not saved; fixed in Settings/CustomFields/Index.
//
// Field names carry the run's time stamp, so nothing clashes with a
// field an earlier run left behind.
// =====================================================================

using MerkaiTrial.E2E.Infrastructure;

namespace MerkaiTrial.E2E.Tests;

[TestFixture, Category("CustomFields")]
public class CustomFieldTests : E2ETest
{
    private static TestTenant D => World.D ?? throw new InvalidOperationException("Workspace D was not built.");

    [OneTimeSetUp]
    public async Task BuildWorkspaceDAsync() => await WorldBuilder.EnsureWorkspaceDAsync();

    // =================================================================
    // 1. Lead → contact and deal
    // =================================================================

    [Test]
    public async Task A_leads_custom_fields_carry_over_to_its_contact_and_deal()
    {
        var budget = $"Budget {World.Stamp}";
        var tier = $"Licence tier {World.Stamp}";
        var admin = await SignedInAsync(D.Admin);

        await Step("Add a Budget number field to Contacts and Deals, and a Licence tier choice to Deals", async () =>
        {
            await AddFieldAsync(admin, "Contact", new FieldSpec(budget, "number"));
            await AddFieldAsync(admin, "Deal", new FieldSpec(budget, "number"));
            // Silver first, so the deal's "Platinum" is not the same
            // position as the lead's: only matching by name gets it right.
            await AddFieldAsync(admin, "Deal", new FieldSpec(tier, "dropdown") { Choices = new[] { "Silver", "Gold", "Platinum" } });
        });

        await Step("Add the same two fields to Leads, copying into the contact and deal fields", async () =>
        {
            await AddFieldAsync(admin, "Lead", new FieldSpec(budget, "number") { CopyToContact = budget, CopyToDeal = budget });
            await AddFieldAsync(admin, "Lead", new FieldSpec(tier, "dropdown") { Choices = new[] { "Gold", "Platinum" }, CopyToDeal = tier });

            var row = admin.Locator(".cf-row", new() { HasText = budget }).First;
            await Assertions.Expect(row).ToContainTextAsync("On conversion");
        });

        var leadName = World.Unique("Kavya Fields");
        var leadId = await Step("Add a lead", () =>
            Flows.CreateLeadAsync(admin, leadName, World.EmailFor("D", $"kavya{Random.Shared.Next(1000, 9999)}"), 500000m, D));

        await Step("Fill in Budget and Licence tier on the lead's Edit page", async () =>
        {
            await admin.GotoAsync($"/Leads/Edit/{leadId}");
            await admin.GetByLabel(budget).FillAsync("1250000");
            await Ui.SelectByTextAsync(admin.GetByLabel(tier), "Platinum");
            await SaveLeadAsync(admin);
        });

        await Step("The lead shows them, the number with Indian grouping", async () =>
        {
            await admin.GotoAsync($"/Leads/Detail/{leadId}");
            await ExpectDetailValueAsync(admin, budget, "12,50,000");
            await ExpectDetailValueAsync(admin, tier, "Platinum");
        });

        await Step("Leads list: filtering on Budget finds the lead, and a higher minimum leaves it out", async () =>
        {
            var match = admin.Locator(".lx-table a", new() { HasText = leadName });

            await admin.GotoAsync("/Leads");
            await FilterByMinimumAsync(admin, budget, "1000000");
            await Assertions.Expect(match.First).ToBeVisibleAsync();

            await FilterByMinimumAsync(admin, budget, "2000000");
            await Assertions.Expect(match).ToHaveCountAsync(0);
        });

        var dealTitle = $"{leadName} deal";
        var message = await Step("Qualify and convert the lead", async () =>
        {
            await admin.GotoAsync($"/Leads/Detail/{leadId}");
            await Flows.QualifyLeadAsync(admin);
            return await Flows.ConvertLeadAsync(admin, dealTitle, 500000m);
        });

        await Step("The conversion says three details came across", () =>
        {
            Assert.That(message, Does.Contain("3 additional details carried over"),
                $"The pipeline's message after converting was: \"{message}\"");
            return Task.CompletedTask;
        });

        var tenantId = await Db.TenantIdAsync(D.Name);
        var dealId = await Db.DealIdByTitleAsync(tenantId, dealTitle);

        await Step("The deal shows Budget 12,50,000 and Licence tier Platinum", async () =>
        {
            await admin.GotoAsync($"/Pipeline/Detail/{dealId}");
            await ExpectDetailValueAsync(admin, budget, "12,50,000");
            await ExpectDetailValueAsync(admin, tier, "Platinum");
        });

        await Step("The new contact shows Budget 12,50,000", async () =>
        {
            var (contactId, _) = await Db.DealContactAsync(dealId);
            Assert.That(contactId, Is.Not.Null, "The converted deal has no contact.");

            await admin.GotoAsync($"/Contacts/Detail?id={contactId}");
            await ExpectDetailValueAsync(admin, budget, "12,50,000");
        });
    }

    // =================================================================
    // 2. A required field
    // =================================================================

    [Test]
    public async Task A_required_lead_field_is_asked_for_and_checked_by_the_server()
    {
        var po = $"PO reference {World.Stamp}";
        var admin = await SignedInAsync(D.Admin);

        var leadName = World.Unique("Arjun Required");
        var leadId = await Step("Add a lead before the field exists", () =>
            Flows.CreateLeadAsync(admin, leadName, World.EmailFor("D", $"arjun{Random.Shared.Next(1000, 9999)}"), 250000m, D));

        var fieldOn = false;
        try
        {
            await Step("Add a required text field to Leads", async () =>
            {
                await AddFieldAsync(admin, "Lead", new FieldSpec(po, "text") { Required = true });
                fieldOn = true;
                await Assertions.Expect(admin.Locator(".cf-row", new() { HasText = po }).First).ToContainTextAsync("Required");
            });

            await Step("The older lead shows it as Not provided", async () =>
            {
                await admin.GotoAsync($"/Leads/Detail/{leadId}");
                await ExpectDetailValueAsync(admin, po, "Not provided");
            });

            var input = admin.GetByLabel(po);

            await Step("Edit: the browser will not save without it", async () =>
            {
                await admin.GotoAsync($"/Leads/Edit/{leadId}");
                Assert.That(await input.EvaluateAsync<bool>("el => el.required"), Is.True,
                    "The required field's input is not marked required.");

                await admin.GetByRole(AriaRole.Button, new() { Name = "Save Changes" }).ClickAsync();
                await admin.WaitForTimeoutAsync(1000);

                Assert.That(Ui.PathOf(admin.Url), Does.Contain("/Leads/Edit"),
                    "The lead saved with a required field empty.");
                Assert.That(await input.EvaluateAsync<bool>("el => el.validity.valueMissing"), Is.True,
                    "The browser did not flag the empty required field.");
            });

            await Step("With the browser's check removed, the server refuses it and names the field", async () =>
            {
                await input.EvaluateAsync<bool>("el => { el.removeAttribute('required'); return true; }");
                await admin.GetByRole(AriaRole.Button, new() { Name = "Save Changes" }).ClickAsync();

                var error = admin.Locator("span.text-danger", new() { HasText = $"{po} is required" });
                try
                {
                    await Assertions.Expect(error.First).ToBeVisibleAsync();
                }
                catch (Exception ex) when (ex is not AssertionException)
                {
                    throw new AssertionException(
                        $"The server did not refuse the empty required field (now at {Ui.PathOf(admin.Url)}). " +
                        await Ui.DescribePageProblemsAsync(admin));
                }
                Assert.That(Ui.PathOf(admin.Url), Does.Contain("/Leads/Edit"));
            });

            await Step("Filled in, it saves and shows on the lead", async () =>
            {
                await admin.GetByLabel(po).FillAsync("PO-4471");
                await SaveLeadAsync(admin);

                await admin.GotoAsync($"/Leads/Detail/{leadId}");
                await ExpectDetailValueAsync(admin, po, "PO-4471");
            });

            await Step("Switched off, it leaves the form and the value stays on the lead, marked Retired", async () =>
            {
                await SwitchOffAsync(admin, "Lead", po);
                fieldOn = false;

                await admin.GotoAsync($"/Leads/Edit/{leadId}");
                await Assertions.Expect(admin.Locator("#Input_FullName")).ToBeVisibleAsync();
                await Assertions.Expect(admin.GetByLabel(po)).ToHaveCountAsync(0);

                await admin.GotoAsync($"/Leads/Detail/{leadId}");
                await ExpectDetailValueAsync(admin, po, "PO-4471");
                await Assertions.Expect(admin.Locator("label", new() { HasText = po }).First).ToContainTextAsync("Retired");
            });
        }
        finally
        {
            if (fieldOn)
            {
                try { await SwitchOffAsync(admin, "Lead", po); }
                catch (Exception ex)
                {
                    TestContext.Progress.WriteLine(
                        $"[e2e] Could not switch \"{po}\" off again in workspace D: {ex.Message}. " +
                        "Lead edits in D will ask for it until it is switched off.");
                }
            }
        }
    }

    // =================================================================
    // Helpers
    // =================================================================

    /// <summary>What to fill in on Settings → Custom fields → Add field.
    /// Type is the radio's value: text, number, date, dropdown, checkbox.</summary>
    private sealed record FieldSpec(string Label, string Type)
    {
        public bool Required { get; init; }
        public string[] Choices { get; init; } = Array.Empty<string>();
        public string? CopyToContact { get; init; }
        public string? CopyToDeal { get; init; }
    }

    /// <summary>Settings → Custom fields → (entity tab) → Add field.
    /// Ends on the tab's list with the green "added" message.</summary>
    private static async Task AddFieldAsync(IPage admin, string entity, FieldSpec f)
    {
        await admin.GotoAsync($"/Settings/CustomFields?entity={entity}&add=true");
        var form = admin.Locator("#cfForm");
        await Assertions.Expect(form).ToBeVisibleAsync();

        // The radios are visually hidden behind their cards; the card (the
        // label) is what a person clicks. Picking the type first also lets
        // the page's script show the "copy to" fields of that type.
        await form.Locator($"label[for='cfType_{f.Type}']").ClickAsync();
        await Assertions.Expect(form.Locator($"#cfType_{f.Type}")).ToBeCheckedAsync();

        await form.Locator("#Input_Label").FillAsync(f.Label);
        if (f.Required) await form.Locator("#Input_IsRequired").CheckAsync();

        if (f.Choices.Length > 0)
        {
            var boxes = form.Locator("[data-cf-options] input[data-cf-name=Label]");
            for (var i = 0; i < f.Choices.Length; i++)
            {
                if (await boxes.CountAsync() <= i)
                    await form.Locator("[data-cf-add-option]").ClickAsync();
                await boxes.Nth(i).FillAsync(f.Choices[i]);
            }
        }

        if (f.CopyToContact is not null)
            await PickCopyTargetAsync(form.Locator("#Input_MapToContactFieldId"), f.CopyToContact, "contact");
        if (f.CopyToDeal is not null)
            await PickCopyTargetAsync(form.Locator("#Input_MapToDealFieldId"), f.CopyToDeal, "deal");

        await form.Locator("button[type=submit]").ClickAsync();
        await Ui.ExpectSuccessAsync(admin, $"\"{f.Label}\" added");
    }

    /// <summary>
    /// 089b. Picks a "copy to" target and checks a person could have picked
    /// it. Playwright will select a greyed-out option, but the browser then
    /// sends nothing for it — the first 089 run saved the field with no
    /// mapping that way, and only failed two screens later.
    /// </summary>
    private static async Task PickCopyTargetAsync(ILocator select, string label, string noun)
    {
        await Ui.SelectByTextAsync(select, label);
        var disabled = await select.EvaluateAsync<bool>(
            "el => { const o = el.options[el.selectedIndex]; return !o || o.disabled; }");
        if (disabled)
            throw new AssertionException(
                $"\"Copy to {noun} field\" lists \"{label}\" but it is greyed out, so it cannot be chosen " +
                "(the form would send no mapping). The type picked above should have switched it on.");
    }

    /// <summary>The one-click switch on a field's row.</summary>
    private static async Task SwitchOffAsync(IPage admin, string entity, string label)
    {
        await admin.GotoAsync($"/Settings/CustomFields?entity={entity}");
        await admin.Locator($"button[aria-label='Switch off {label}']").ClickAsync();
        await Ui.ExpectSuccessAsync(admin, "switched off");
    }

    /// <summary>Save Changes on Leads → Edit, and the green message on the lead.</summary>
    private static async Task SaveLeadAsync(IPage admin)
    {
        await admin.GetByRole(AriaRole.Button, new() { Name = "Save Changes" }).ClickAsync();
        await Ui.ExpectSuccessAsync(admin, "Lead updated");
    }

    /// <summary>
    /// On a Detail page's "Additional details" card: the value printed
    /// under the field's name. Contains, so "12,50,000" matches whatever
    /// the card puts round it.
    /// </summary>
    private static async Task ExpectDetailValueAsync(IPage page, string label, string expected)
    {
        var value = page.Locator($"label:has-text('{label}') + div.fw-semibold").First;
        try
        {
            await Assertions.Expect(value).ToContainTextAsync(expected);
        }
        catch (Exception ex) when (ex is not AssertionException)
        {
            var shown = await page.Locator($"label:has-text('{label}') + div.fw-semibold").AllInnerTextsAsync();
            throw new AssertionException(
                $"On {Ui.PathOf(page.Url)}, \"{label}\" should show \"{expected}\" but " +
                (shown.Count == 0
                    ? "the page has no such field in its Additional details card."
                    : $"it shows \"{string.Join(" | ", shown.Select(s => s.Trim()))}\"."));
        }
    }

    /// <summary>
    /// The list page's "Filter by additional details" panel: a number
    /// field's "at least" box, then Apply filters. Waits for the list to
    /// reload with the filter in its address.
    /// </summary>
    private static async Task FilterByMinimumAsync(IPage page, string label, string minimum)
    {
        var toggle = page.Locator("button[aria-controls=cffPanel]");
        if (await toggle.CountAsync() == 0)
            throw new AssertionException(
                $"{Ui.PathOf(page.Url)} has no \"Filter by additional details\" panel, although the workspace has custom fields.");

        var box = page.Locator($"#cffPanel input[aria-label='{label} at least']");
        if (!await box.IsVisibleAsync())
        {
            await toggle.ClickAsync();
            await Assertions.Expect(box).ToBeVisibleAsync();
        }

        await box.FillAsync(minimum);
        await page.Locator("#cffPanel button[type=submit]").ClickAsync();
        await Ui.WaitForAddressAsync(page, u => u.Contains("_min=" + minimum, StringComparison.OrdinalIgnoreCase));
    }
}
