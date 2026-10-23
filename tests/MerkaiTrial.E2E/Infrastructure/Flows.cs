// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Infrastructure/Flows.cs
//
// Multi-step things more than one test does, written once.
//
// 087b: CreateLeadAsync reports the page's own error when Add lead is
// refused, instead of timing out.
//
// 088: the rest of the journey as reusable steps — qualify, convert,
// fill a quote (tax and discount included), save or send it, read the
// customer link, accept it as the customer. CreateLeadAsync takes the
// workspace, so a Thailand lead is THB with a Thai phone number. The
// original SalesPipelineTests keeps its own inline copy; it passes and
// is left alone.
//
// 092:
//   • CreateLeadAsync takes an optional beforeSave step — the moment to
//     fill in the lead's custom fields, which are on Add lead now.
//   • OpenLeadAsync waits with Ui.WaitForPageAsync: the lead's page needs
//     to be usable, not fully loaded, and if it is slow the failure says
//     the SERVER was slow (one 089 run lost 20 seconds here and reported
//     it as a missing page).
//
// 093: Add lead can now answer "this looks like a lead you already
// have". Every workspace's test leads share one sample phone number, so
// most adds see it. CreateLeadAsync presses "Save as a new lead anyway"
// by default (acceptDuplicate: true); a test about the warning passes
// false, and then a warning fails the call with the warning's text — the
// page is left on the form, with the warning showing.
//
// 093b: the required-field asterisks (span.text-danger, "*") are no longer
// mistaken for an error. The first 093 run stopped at every warning with
// "Add lead was refused: * | * | * | *".
//
// 099: ConvertLeadAsync waits for the pipeline page's DOMContentLoaded,
// not its full "load" — a stalled CDN font failed one full run. See there.
//
// 100: the longer journeys several new tests need, written once —
//   • LeadToDealAsync: add, qualify, convert; returns the lead and deal ids.
//   • SendQuoteAsync: a quote for a deal, saved and sent; returns its id.
//   • IssuedInvoiceAsync: the customer accepts a sent quote, Create
//     invoice, Issue; returns the invoice's id.
//   The same steps SalesPipelineTests takes, as reusable pieces.
// =====================================================================

using System.Globalization;
using System.Text.RegularExpressions;

namespace MerkaiTrial.E2E.Infrastructure;

/// <summary>One quote line. TaxRate and DiscountPercent are percentages
/// (18 for 18%); null leaves whatever the editor fills in.</summary>
public sealed record QuoteLine(string Name, decimal Quantity, decimal UnitPrice,
    decimal? TaxRate = null, decimal? DiscountPercent = null);

public static class Flows
{
    /// <summary>
    /// A status that counts as Qualified, by name: the default "Qualified",
    /// or the name WorkspaceSettingsTests renames it to for a while.
    /// "Disqualified", "Unqualified" and "Not qualified" never match.
    /// </summary>
    public const string QualifiedPattern = @"qualified|ready to buy";
    public const string NotQualifiedPattern = @"(dis|un|not\s*)qualified";

    /// <summary>The stages Quotes → New offers deals from.</summary>
    public const string QuotableStagePattern = @"discovery|qualification|proposal";

    // ── Leads ─────────────────────────────────────────────────────────

    /// <summary>
    /// Leads → Add lead in the workspace's own country and currency
    /// (workspace A — India / INR — when none is given), then finds it in
    /// the list and opens it. Returns the lead's id; the page is left on
    /// its detail screen.
    /// </summary>
    /// <param name="beforeSave">092. Runs on the filled form just before Create Lead — custom fields.</param>
    /// <param name="acceptDuplicate">
    /// 093. True: a duplicate warning is answered with "Save as a new lead
    /// anyway". False: it fails the call with what the warning said.
    /// </param>
    public static async Task<Guid> CreateLeadAsync(IPage page, string fullName, string email, decimal value,
        TestTenant? workspace = null, string? companyName = null, Func<IPage, Task>? beforeSave = null,
        bool acceptDuplicate = true)
    {
        var country  = workspace?.Country ?? "India";
        var currency = workspace?.Currency ?? "INR";
        var phone    = workspace?.SamplePhone ?? "+919800000000";

        await page.GotoAsync("/Leads/Create");
        await page.Locator("#Input_FullName").FillAsync(fullName);
        await page.Locator("#Input_Email").FillAsync(email);
        await page.Locator("#Input_Phone").FillAsync(phone);
        await page.Locator("#Input_CompanyName").FillAsync(companyName ?? fullName + " Pvt Ltd");

        // Country first: choosing it fills the currency on this page.
        if (await Ui.SelectByTextAsync(page.Locator("#Input_CountryId"), country, required: false) is null)
            await Ui.SelectFirstIfEmptyAsync(page.Locator("#Input_CountryId"));
        if (await Ui.SelectByTextAsync(page.Locator("#Input_CurrencyId"), currency, required: false) is null)
            await Ui.SelectFirstIfEmptyAsync(page.Locator("#Input_CurrencyId"));
        await Ui.SelectFirstIfEmptyAsync(page.Locator("#Input_ChannelId"));

        await page.Locator("#Input_EstimatedValue").FillAsync(value.ToString(CultureInfo.InvariantCulture));

        if (beforeSave is not null) await beforeSave(page);     // 092

        await page.GetByRole(AriaRole.Button, new() { Name = "Create Lead" }).ClickAsync();

        // 087b: either the page moves on, or it comes back with an error.
        // 093: …or with the duplicate warning.
        //
        // 093b: the red "*" beside each required label is ALSO a
        // span.text-danger. It never mattered while a successful save left
        // the page; the duplicate warning keeps the form on screen, and four
        // asterisks then read as "Add lead was refused: * | * | * | *".
        // A lone asterisk is not a problem.
        var problem = page.Locator(".alert-danger:visible, .validation-summary-errors:visible, span.text-danger:not(:empty)")
            .Filter(new() { HasNotTextRegex = new Regex(@"^\s*\*\s*$") });
        var duplicate = page.Locator("#dupWarning");
        await Ui.WaitUntilAsync(page,
            async () => !Ui.PathOf(page.Url).EndsWith("/Leads/Create", StringComparison.OrdinalIgnoreCase)
                        || await problem.CountAsync() > 0
                        || await duplicate.CountAsync() > 0,
            "Add lead neither saved nor showed an error.");

        if (Ui.PathOf(page.Url).EndsWith("/Leads/Create", StringComparison.OrdinalIgnoreCase)
            && await problem.CountAsync() == 0 && await duplicate.CountAsync() > 0)
        {
            var warning = (await duplicate.InnerTextAsync()).Trim();
            if (!acceptDuplicate)
                throw new AssertionException("Add lead warned about a possible duplicate: " + warning);

            await page.Locator("#saveAnyway").ClickAsync();
            await Ui.WaitUntilAsync(page,
                async () => !Ui.PathOf(page.Url).EndsWith("/Leads/Create", StringComparison.OrdinalIgnoreCase)
                            || await problem.CountAsync() > 0,
                "\"Save as a new lead anyway\" neither saved nor showed an error.");
        }

        if (Ui.PathOf(page.Url).EndsWith("/Leads/Create", StringComparison.OrdinalIgnoreCase))
        {
            var texts = await problem.AllInnerTextsAsync();
            throw new AssertionException(
                "Add lead was refused: " + string.Join(" | ", texts.Select(t => t.Trim()).Where(t => t.Length > 0)));
        }

        return await OpenLeadAsync(page, fullName);
    }

    /// <summary>Searches the leads list for a name and opens the match.</summary>
    public static async Task<Guid> OpenLeadAsync(IPage page, string fullName)
    {
        await page.GotoAsync("/Leads?SearchTerm=" + Uri.EscapeDataString(fullName));
        var link = page.Locator(".lx-table a", new PageLocatorOptions { HasText = fullName }).First;
        await link.ClickAsync();
        await Ui.WaitForPageAsync(page, u => u.Contains("/Leads/Detail", StringComparison.OrdinalIgnoreCase),
            $"The lead \"{fullName}\"");      // 092
        return Ui.IdFromUrl(page.Url);
    }

    /// <summary>
    /// On a lead's detail page: picks a Qualified status from the status
    /// drop-down (it submits itself) and waits for the Convert button.
    /// Returns the option's text.
    /// </summary>
    public static async Task<string> QualifyLeadAsync(IPage page, string pattern = QualifiedPattern)
    {
        var status = page.Locator("#statusChangeForm select[name=status]");
        var chosen = await Ui.SelectByPatternAsync(status, pattern, excludePattern: NotQualifiedPattern);
        await Assertions.Expect(page.Locator("a[href*='ConvertToDeal']").First)
            .ToBeVisibleAsync(new() { Timeout = E2ESettings.ActionTimeoutMs });
        return chosen;
    }

    /// <summary>
    /// From a qualified lead's detail page: Convert → deal title, value,
    /// close date in 30 days, a stage whose label matches
    /// <paramref name="stagePattern"/> (the first stage when none does) →
    /// Create the deal. Ends on the pipeline board. Returns the green
    /// message the board shows ("Lead converted. New contact and company
    /// created. …"), or "" if it shows none.
    /// </summary>
    public static async Task<string> ConvertLeadAsync(IPage page, string dealTitle, decimal value,
        string stagePattern = QuotableStagePattern)
    {
        await page.Locator("a[href*='ConvertToDeal']").First.ClickAsync();
        await page.Locator("#Input_DealTitle").FillAsync(dealTitle);
        await page.Locator("#Input_ExpectedValue").FillAsync(value.ToString(CultureInfo.InvariantCulture));
        await page.Locator("#Input_ExpectedCloseDate").FillAsync(
            DateTime.Today.AddDays(30).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        var stage = page.Locator("label.lc-stage", new() { HasTextRegex = new(stagePattern, RegexOptions.IgnoreCase) });
        if (await stage.CountAsync() == 0) stage = page.Locator("label.lc-stage");
        await stage.First.ClickAsync();
        await Assertions.Expect(page.Locator("input[name='Input.Stage']:checked")).ToHaveCountAsync(1);

        await page.GetByRole(AriaRole.Button, new() { Name = "Create the deal" }).ClickAsync();

        try
        {
            // 099: DOMContentLoaded, not the full "load" event. One full run
            // reached /Pipeline (the server answered 200 in 0.3s) and still
            // failed here: "load" also waits for every font and stylesheet
            // from the CDNs, and one of those stalled for 20 seconds. The
            // page was there; the test was waiting for a font.
            //
            // 101c: and not page.WaitForURLAsync at all. When the click had
            // already followed the redirect to the end, that waited for a
            // DOMContentLoaded which had already happened — 20 s on a page
            // that was finished. Ui.WaitForAddressAsync looks instead.
            await Ui.WaitForAddressAsync(page, u => Ui.PathOf(u).StartsWith("/Pipeline", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (Ui.IsTimeout(ex))
        {
            throw new AssertionException("Create the deal did not reach the pipeline. " +
                                         await Ui.DescribePageProblemsAsync(page));
        }

        return await Ui.TextOrEmptyAsync(page.Locator(".alert-success"));
    }

    // ── Quotes ────────────────────────────────────────────────────────

    /// <summary>
    /// Quotes → New for a deal, and fills the one starter line. Leaves the
    /// editor open with its live totals, for the caller to read or submit
    /// (SubmitQuoteAsync).
    /// </summary>
    public static async Task FillQuoteAsync(IPage page, string dealTitle, QuoteLine line)
    {
        await page.GotoAsync("/Quotes/Create");
        await Ui.SelectByTextAsync(page.Locator("#SelectedDealId"), dealTitle);
        await page.GetByRole(AriaRole.Button, new() { Name = "Continue" }).ClickAsync();

        // Wait for the editor to finish drawing before looking for the
        // starter line, or the check runs too early and adds a second one
        // (087d).
        await Assertions.Expect(page.Locator("#qcForm")).ToBeVisibleAsync();
        var rows = page.Locator("#qeBody tr:has([data-f=name])");
        try
        {
            await Ui.WaitUntilAsync(page, async () => await rows.CountAsync() > 0, "no starter line", timeoutMs: 8_000);
        }
        catch (AssertionException)
        {
            await page.Locator("#qeAddLine").ClickAsync();
        }
        await Assertions.Expect(rows).ToHaveCountAsync(1);

        var inv = CultureInfo.InvariantCulture;
        await rows.First.Locator("[data-f=name]").FillAsync(line.Name);
        await rows.First.Locator("[data-f=quantity]").FillAsync(line.Quantity.ToString(inv));
        await rows.First.Locator("[data-f=unitPrice]").FillAsync(line.UnitPrice.ToString(inv));

        if (line.TaxRate is decimal tax)
            await rows.First.Locator("[data-f=taxRate]").FillAsync(tax.ToString(inv));

        if (line.DiscountPercent is decimal pct)
        {
            // % mode first: the one box holds an amount or a percentage
            // depending on it. The row may redraw on the change, so it is
            // looked up again rather than reused.
            await rows.First.Locator("[data-f=discountMode]").SelectOptionAsync("pct");
            await rows.First.Locator("[data-f=discountInput]").FillAsync(pct.ToString(inv));
        }
    }

    /// <summary>
    /// Presses "Save &amp; send to customer" (or, when the editor relabelled
    /// it, "Save &amp; submit for approval") — or "Save as draft" when
    /// <paramref name="send"/> is false — and waits for the quote to open.
    /// Returns the green message the quote page shows.
    /// </summary>
    public static async Task<string> SubmitQuoteAsync(IPage page, bool send = true)
    {
        if (send)
            await page.Locator("#qcSend").ClickAsync();
        else
            await page.Locator("#qcForm button[name=sendToCustomer][value=false]").ClickAsync();

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!page.Url.Contains("/Quotes/Detail", StringComparison.OrdinalIgnoreCase))
        {
            if (DateTime.UtcNow > deadline)
                throw new AssertionException(
                    $"Saving the quote did not open it (still on {Ui.PathOf(page.Url)}). " +
                    await Ui.DescribePageProblemsAsync(page));
            await page.WaitForTimeoutAsync(500);
        }

        // 101d: looked at, not waited for as an event — see Ui.WaitForDocumentAsync.
        await Ui.WaitForDocumentAsync(page);
        return await Ui.TextOrEmptyAsync(page.Locator(".alert-success"));
    }

    /// <summary>On a sent quote's page: the customer link, pointed at the
    /// test server. Fails with what the page says when there is none.</summary>
    public static async Task<string> CustomerLinkAsync(IPage page)
    {
        var link = page.Locator("#qdLink");
        try
        {
            await Assertions.Expect(link).ToBeVisibleAsync();
        }
        catch (Exception ex) when (ex is not AssertionException)
        {
            throw new AssertionException(
                "The quote has no customer link, so it was not sent. " + await Ui.DescribePageProblemsAsync(page));
        }

        var text = (await link.InnerTextAsync()).Trim();
        Assert.That(text, Does.Contain("/q/"), "The customer link does not look like /q/{token}.");
        return Ui.Rebase(text);
    }

    // ── 100: whole journeys ───────────────────────────────────────────

    /// <summary>
    /// Add a lead in <paramref name="workspace"/>, qualify it and convert it
    /// to a deal in a stage quotes can be raised from. Returns both ids;
    /// the page ends on the pipeline board.
    /// </summary>
    public static async Task<(Guid LeadId, Guid DealId)> LeadToDealAsync(IPage page, TestTenant workspace,
        string leadName, string email, string dealTitle, decimal value, string? companyName = null)
    {
        var leadId = await CreateLeadAsync(page, leadName, email, value, workspace, companyName);
        await QualifyLeadAsync(page);
        await ConvertLeadAsync(page, dealTitle, value);
        var tenantId = await GlobalSetup.Db.TenantIdAsync(workspace.Name);
        var dealId = await GlobalSetup.Db.DealIdByTitleAsync(tenantId, dealTitle);
        return (leadId, dealId);
    }

    /// <summary>
    /// A one-line quote for the deal, saved and sent to the customer.
    /// Returns the quote's id; the page is left on the quote, showing its
    /// customer link.
    /// </summary>
    public static async Task<Guid> SendQuoteAsync(IPage page, string dealTitle, QuoteLine line)
    {
        await FillQuoteAsync(page, dealTitle, line);
        await SubmitQuoteAsync(page, send: true);
        await CustomerLinkAsync(page);                 // fails, with the page's reason, if it was not sent
        return Ui.IdFromUrl(page.Url);
    }

    /// <summary>
    /// From a SENT quote: the customer accepts it on its public page,
    /// then Create invoice and Issue invoice. Returns the invoice's id; the
    /// page is left on the issued invoice.
    /// </summary>
    public static async Task<Guid> IssuedInvoiceAsync(IPage page, IPage customer, Guid quoteId)
    {
        var quoteUrl = $"/Quotes/Detail?id={quoteId}";
        await page.GotoAsync(quoteUrl);
        var link = await CustomerLinkAsync(page);
        await AcceptAsCustomerAsync(customer, link);

        await page.GotoAsync(quoteUrl);
        await page.GetByRole(AriaRole.Button, new() { Name = "Create invoice" }).ClickAsync();

        // Either straight to the invoice, or back on the quote with
        // "A draft invoice exists — Open draft".
        var openDraft = page.Locator("main a[href*='/Invoices/Detail']");
        await Ui.WaitUntilAsync(page,
            async () => page.Url.Contains("/Invoices/Detail", StringComparison.OrdinalIgnoreCase)
                        || await openDraft.CountAsync() > 0,
            "No invoice appeared after Create invoice.");
        if (!page.Url.Contains("/Invoices/Detail", StringComparison.OrdinalIgnoreCase))
        {
            await openDraft.First.ClickAsync();
            await Ui.WaitForAddressAsync(page, u => u.Contains("/Invoices/Detail", StringComparison.OrdinalIgnoreCase));
        }

        // 101: the button MUST be there. A new invoice is a draft (number
        // DRAFT-…, nothing sent) until it is issued. Before 101 this step
        // said "if there is an Issue button", and that hid a bug for many
        // rounds: invoices were being saved already Sent (see
        // FlowDbContext.ApplyAlwaysSendValues), so there was no button,
        // and the test carried on as if all was well.
        var issue = page.GetByRole(AriaRole.Button, new() { Name = "Issue invoice" });
        await Assertions.Expect(issue).ToHaveCountAsync(1, new()
        {
            Timeout = E2ESettings.ActionTimeoutMs,
        });
        await issue.ClickAsync();   // the confirm() is accepted by the session
        await Assertions.Expect(issue).ToHaveCountAsync(0, new() { Timeout = E2ESettings.ActionTimeoutMs });

        return Ui.IdFromUrl(page.Url);
    }

    /// <summary>Opens the public quote in a signed-out session and accepts it.</summary>
    public static async Task AcceptAsCustomerAsync(IPage customer, string publicLink)
    {
        await customer.GotoAsync(publicLink);
        await customer.Locator(".btn-accept").ClickAsync();
        await Assertions.Expect(customer.GetByText("You have accepted this quote")).ToBeVisibleAsync();
    }
}
