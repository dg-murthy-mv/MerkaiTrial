// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Public/QuoteView.cshtml.cs
//
// REVISED (059). The page a CUSTOMER sees. No login, no tenant claims.
//
// WHY THIS ROUND TOUCHED IT AT ALL: rounds 052, 053 and 055 changed the
// shape of a quote line and never came back here, so the one page a
// customer reads was the last one running on the old assumptions:
//
//   052  Quantity int -> decimal(18,4).  The view printed @item.Quantity
//        and a decimal materialised at scale 4 keeps its zeros, so every
//        line read "2.0000". QuoteItemDto.QuantityDisplay was added in
//        052 for exactly this and went unused here.
//   053  CompanyName / ContactName split. CompanyName is now legitimately
//        EMPTY for a person-to-person sale, and this page printed only
//        CompanyName — so those quotes rendered with a blank line under
//        the quote number.
//   055  Per-line discounts. The summary box showed DiscountTotal but no
//        line showed its own discount, so unit price x qty did not reach
//        the line total and nothing on the page explained the gap.
//
// TWO SECURITY FIXES, both the same shape as ones already accepted
// elsewhere in the product:
//
//   1. THE TOKEN IS NO LONGER LOGGED. It was going into the log on every
//      accept/decline failure: _logger.LogError(ex, "...{Token}", Token).
//      That token IS the credential for the quote — it is the whole of
//      the /q/{token} URL's security. Anyone with log access could open
//      the customer's quote and accept it as them. Masked to the last
//      four, the same rule as the phone numbers in WhatsApp sending: a
//      log is for finding the row, not for holding the key to it.
//
//   2. ACCEPT/DECLINE NOW CHECK STATE SERVER-SIDE. The action bar is
//      correctly hidden when a quote is expired or already decided, but a
//      hidden button is not a control — a replayed or hand-made POST went
//      straight through to UpdateStatusByTokenAsync. Whether that service
//      validates is in code this round did not see, so the guard is here:
//      re-read the quote, refuse if expired or already Accepted/Rejected,
//      and only then act. Belt and braces, and correct either way.
//
// STILL NO TENANT CONTEXT, deliberately. This is [AllowAnonymous] and a
// plain PageModel; ICurrentTenantService cannot resolve a country here
// and AuthorizedPageModel.FormatCurrency is unavailable. Everything the
// page formats comes off the quote itself.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Quotes;
using MerkaiTrial.Application.Configuration;   // 067: TaxCodes
using MerkaiTrial.Application.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Globalization;
using System.Linq;   // HasAnyLineDiscount — explicit, not relying on ImplicitUsings

namespace MerkaiTrial.Admin.Web.Pages.Public
{
    [AllowAnonymous]
    public class QuoteViewModel : PageModel
    {
        private readonly IQuoteService           _quoteService;
        private readonly ILogger<QuoteViewModel> _logger;

        public QuoteViewModel(
            IQuoteService           quoteService,
            ILogger<QuoteViewModel> logger)
        {
            _quoteService = quoteService;
            _logger       = logger;
        }

        [BindProperty(SupportsGet = true)]
        public string Token { get; set; } = string.Empty;

        public QuoteDto? Quote      { get; set; }
        public bool      IsExpired  { get; set; }
        public bool      IsAccepted { get; set; }
        public bool      IsRejected { get; set; }

        /// <summary>
        /// True when the customer can still act. One expression, used by
        /// the view to draw the action bar AND by the POST handlers to
        /// decide whether to honour it — so the button and the rule behind
        /// it can never drift apart.
        /// </summary>
        public bool CanDecide => Quote is not null && !IsAccepted && !IsRejected && !IsExpired;

        // ── Who the quote is for ──────────────────────────────────────
        // 053 left CompanyName empty for a genuinely person-to-person
        // sale. Not CustomerNaming.Display(): that works on Deal/Contact
        // entities, and by the time a QuoteDto exists the naming is
        // already resolved — this is only the fallback between the two
        // fields the DTO carries.

        /// <summary>The company, or the person when there is no company.</summary>
        public string CustomerHeading
        {
            get
            {
                if (Quote is null) return string.Empty;

                if (!string.IsNullOrWhiteSpace(Quote.CompanyName))
                    return Quote.CompanyName.Trim();

                return Quote.ContactName?.Trim() ?? string.Empty;
            }
        }

        /// <summary>
        /// The person, when a company is already on the line above.
        /// Null when there is no company (the person IS the heading) or no
        /// contact — so the view never prints an empty second line.
        /// </summary>
        public string? CustomerSubheading
        {
            get
            {
                if (Quote is null) return null;
                if (string.IsNullOrWhiteSpace(Quote.CompanyName)) return null;

                var person = Quote.ContactName?.Trim();
                return string.IsNullOrWhiteSpace(person) ? null : person;
            }
        }

        // ── 065: who the quote is FROM ────────────────────────────────
        //
        // This page had no way to name the seller. A customer opened a
        // ฿25,246.65 quote, saw their own company, the prices and an
        // Accept button, and nothing identifying who sent it — no name, no
        // address, no number to call. The only sender-ish text on the page
        // was "Powered by Merkai CRM", which is the product's name, not
        // the workspace's.
        //
        // It cannot be resolved here: the page is [AllowAnonymous] and a
        // plain PageModel, so ICurrentTenantService would throw. It
        // travels on the DTO, filled in by GetQuoteByTokenHandler where
        // the tenant is known. Null on an older cached response, so every
        // use below is guarded.

        public QuoteSellerDto? Seller => Quote?.Seller;

        /// <summary>
        /// True when there is enough to draw a letterhead. A name alone is
        /// not one, and an empty bordered box looks more broken than no box
        /// — the same test Tenant.HasCompanyProfile and the Company Profile
        /// page's live preview use.
        /// </summary>
        public bool ShowSeller => Seller?.HasDetails == true;

        /// <summary>
        /// The country's word for the tax — "VAT" in Thailand, "GST" in
        /// India — for the line column and the totals row. Both said the
        /// generic "Tax" before this round, on a document an Indian
        /// customer may hand to their accountant.
        ///
        /// Falls back to "Tax" rather than to the tenant service, which
        /// this page cannot call.
        /// </summary>
        public string TaxLabel =>
            string.IsNullOrWhiteSpace(Seller?.TaxLabel) ? "Tax" : Seller!.TaxLabel;

        public string CurrencySymbol => Quote?.Currency switch
        {
            "THB" => "฿",
            "INR" => "₹",
            "PHP" => "₱",
            "AED" => "د.إ",
            "USD" => "$",
            "EUR" => "€",
            "GBP" => "£",
            _     => Quote?.Currency ?? ""
        };

        // ── TENANT-AWARE MONEY, WITHOUT A TENANT CONTEXT ──────────────
        // The view once did @Model.CurrencySymbol@x.ToString("N2") with no
        // culture, which falls back to the SERVER's CurrentCulture — so an
        // Indian dev box rendered Thai and UAE customer quotes with lakh
        // grouping (฿6,41,037.00). The quote carries its own currency, so
        // the culture comes from that and no tenant lookup is needed.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CultureInfo> _cultureCache = new();

        private static CultureInfo CultureForCurrency(string? currencyCode)
        {
            if (string.IsNullOrWhiteSpace(currencyCode))
                return CultureInfo.InvariantCulture;

            return _cultureCache.GetOrAdd(currencyCode, code => code switch
            {
                "INR" => new CultureInfo("en-IN"),   // lakh grouping: {3,2}
                "PHP" => new CultureInfo("en-PH"),
                "THB" => new CultureInfo("th-TH"),
                "AED" => new CultureInfo("en-AE"),
                "USD" => new CultureInfo("en-US"),
                "EUR" => new CultureInfo("en-IE"),
                "GBP" => new CultureInfo("en-GB"),
                _     => CultureInfo.InvariantCulture
            });
        }

        /// <summary>
        /// Symbol + amount grouped per the quote's currency.
        /// Use this in the view instead of .ToString("N2").
        /// </summary>
        public string FormatCurrency(decimal amount, int decimals = 2)
            => $"{CurrencySymbol}{amount.ToString($"N{decimals}", CultureForCurrency(Quote?.Currency))}";

        /// <summary>
        /// 059: a tax rate the customer can trust. The view had
        /// (TaxRate * 100).ToString("N0"), which printed a 12.5% line as
        /// "13%" and a 0.5% line as "1%" — a rounded tax rate on a
        /// document the customer may treat as an offer.
        ///
        /// QuoteItemDto.TaxRate is a FRACTION (0.18 = 18%). Product.TaxRate
        /// is a percent. Three conventions live in this codebase; this one
        /// is the fraction.
        ///
        /// "0.##" so 18 prints "18" and 12.5 prints "12.5". The quote's own
        /// culture, so the decimal separator matches the money beside it.
        /// </summary>
        public string FormatTaxRate(decimal fraction)
            => (fraction * 100m).ToString("0.##", CultureForCurrency(Quote?.Currency)) + "%";

        /// <summary>
        /// 059: what this line's discount was, in the terms it was agreed.
        /// DiscountLabel already carries its own minus sign ("-10%"), so
        /// nothing is prepended to it — only to the money fallback.
        /// An em dash when there is no discount: a blank cell in a money
        /// column reads as missing data rather than as zero.
        /// </summary>
        public string FormatLineDiscount(QuoteItemDto item)
        {
            if (!string.IsNullOrWhiteSpace(item.DiscountLabel))
                return item.DiscountLabel!;

            if (item.LineDiscount > 0)
                return "-" + FormatCurrency(item.LineDiscount);

            return "—";
        }

        /// <summary>True when any line on the quote has a discount.</summary>
        public bool HasAnyLineDiscount =>
            Quote is not null &&
            Quote.Items.Any(i => i.LineDiscount > 0 || i.DiscountPercent.HasValue);

        /// <summary>
        /// 067: true when any line carries a tax classification code, which
        /// is the only condition under which the column is drawn — exactly
        /// the rule the Discount column above has followed since 059, and
        /// exactly the rule both PDFs use. Three of Merkai's four markets
        /// ask for no code, and an empty column on the one page a CUSTOMER
        /// reads looks like something the sender forgot to fill in.
        /// </summary>
        public bool HasAnyLineTaxCode =>
            Quote is not null && TaxCodes.AnyPresent(Quote.Items.Select(i => i.TaxCode));

        /// <summary>
        /// 067: the column heading — "HSN / SAC" on a quote priced in INR,
        /// "Tax code" elsewhere. Driven by the QUOTE's currency, because
        /// this page has no tenant context at all (it is [AllowAnonymous]),
        /// and the quote is the only thing it knows.
        /// </summary>
        public string TaxCodeLabel => TaxCodes.ColumnHeaderFor(Quote?.Currency);

        /// <summary>
        /// 059: the last four characters, for the log. Never the token.
        /// Short or empty tokens degrade to "****" rather than leaking a
        /// prefix of a short one.
        /// </summary>
        private string MaskedToken =>
            string.IsNullOrWhiteSpace(Token) || Token.Length < 8
                ? "****"
                : "****" + Token[^4..];

        // ── GET ───────────────────────────────────────────────────────
        public async Task<IActionResult> OnGetAsync(CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(Token))
                return NotFound();

            Quote = await _quoteService.GetByTokenAsync(Token);

            if (Quote == null)
                return NotFound();

            ApplyState(Quote);

            return Page();
        }

        // ── POST: Customer Accepts ────────────────────────────────────
        public Task<IActionResult> OnPostAcceptAsync(CancellationToken ct)
            => DecideAsync("Accepted", ct);

        // ── POST: Customer Declines ───────────────────────────────────
        public Task<IActionResult> OnPostDeclineAsync(CancellationToken ct)
            => DecideAsync("Rejected", ct);

        /// <summary>
        /// 059: both decisions, one path, with the state check the hidden
        /// buttons were standing in for.
        ///
        /// The re-read is the point. The quote may have expired, or been
        /// accepted in another tab, or in a replayed POST from an old
        /// page, between the GET that drew the buttons and this request.
        /// Redirecting rather than erroring is deliberate: the page the
        /// customer lands on states the real status in its own banner,
        /// which is a better answer than an error for someone who just
        /// double-clicked.
        /// </summary>
        private async Task<IActionResult> DecideAsync(string newStatus, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(Token))
                return NotFound();

            try
            {
                var current = await _quoteService.GetByTokenAsync(Token);
                if (current == null)
                    return NotFound();

                Quote = current;
                ApplyState(current);

                if (!CanDecide)
                {
                    // Already decided, or expired. Not an error — the
                    // banner on the redirected page says which.
                    _logger.LogInformation(
                        "Ignored {Status} for quote {Number} via token {Token}: status {Current}, expires {Expires:u}",
                        newStatus, current.Number, MaskedToken, current.Status, current.ExpiresAtUtc);

                    return RedirectToPage("/Public/QuoteView", new { Token });
                }

                var ok = await _quoteService.UpdateStatusByTokenAsync(Token, newStatus);
                if (!ok)
                    return NotFound();

                // Explicit page path clears ?handler=... from the URL.
                return RedirectToPage("/Public/QuoteView", new { Token });
            }
            catch (Exception ex)
            {
                // The token is NOT in this message. See the header note.
                _logger.LogError(ex,
                    "Failed to set quote status {Status} via token {Token}",
                    newStatus, MaskedToken);

                return RedirectToPage("/Public/QuoteView", new { Token });
            }
        }

        /// <summary>
        /// The three status flags, derived in one place so the GET and the
        /// POST guard can never disagree about what "expired" means.
        /// </summary>
        private void ApplyState(QuoteDto q)
        {
            IsAccepted = q.Status == "Accepted";
            IsRejected = q.Status == "Rejected";

            // Expired only matters while the quote is still open — an
            // accepted quote past its validity date is accepted, not
            // expired, and must not show the "contact us" banner.
            IsExpired = q.ExpiresAtUtc < DateTime.UtcNow
                        && q.Status is "Draft" or "Sent" or "Viewed";
        }
    }
}
