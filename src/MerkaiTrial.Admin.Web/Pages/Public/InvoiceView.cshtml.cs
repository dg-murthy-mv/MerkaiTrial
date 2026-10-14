// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Public/InvoiceView.cshtml.cs
//
// NEW FILE (085). The page a CUSTOMER sees when they open the link in an
// invoice email: /i/{token}. No login, no tenant claims — the twin of
// Public/QuoteView, built on the same rules.
//
// WHAT IT SHOWS
//   Who it is from (the seller's letterhead), who it is for, the lines,
//   the totals, how much has been paid and what is still due, the due
//   date, and a "Download PDF" button for the PDF as it was emailed.
//   Read-only: there is nothing to accept or decline on an invoice.
//
// WHAT IT NEVER SHOWS
//   Payment methods, references or notes (the workspace's bookkeeping —
//   the API sends only the total paid), the workspace's other records,
//   or custom fields nobody chose to print.
//
// THE TOKEN IS THE CREDENTIAL
//   It is the whole security of this URL, so it is never logged whole —
//   the last four characters only, like /q/ links since 061, and the
//   request log masks /i/{token} through PublicLinkPaths.
//
// NO TENANT CONTEXT, deliberately: [AllowAnonymous], a plain PageModel and
// Layout = null, because _Layout and ICurrentTenantService both throw for
// a visitor with no tenant claim. Everything the page formats comes off
// the invoice itself — money in the invoice's own currency and culture.
// =====================================================================

using System.Globalization;
using MerkaiTrial.Admin.Web.Services.Invoices;
using MerkaiTrial.Application.Configuration;   // TaxCodes, CurrencyConfiguration
using MerkaiTrial.Application.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MerkaiTrial.Admin.Web.Pages.Public
{
    [AllowAnonymous]
    public class InvoiceViewModel : PageModel
    {
        private readonly IInvoiceService _invoices;
        private readonly ILogger<InvoiceViewModel> _logger;

        public InvoiceViewModel(IInvoiceService invoices, ILogger<InvoiceViewModel> logger)
        {
            _invoices = invoices;
            _logger = logger;
        }

        [BindProperty(SupportsGet = true)]
        public string Token { get; set; } = string.Empty;

        public PublicInvoiceDto? Data { get; private set; }
        public InvoiceDto? Invoice => Data?.Invoice;
        public QuoteSellerDto? Seller => Data?.Seller;

        /// <summary>Set by the PDF handler when the file could not be fetched.</summary>
        public bool PdfFailed { get; private set; }

        // ── State ─────────────────────────────────────────────────────
        public bool IsVoid => Invoice?.Status == "Cancelled";
        public bool IsPaid => Invoice is not null && !IsVoid && (Invoice.Status == "Paid" || Invoice.Balance <= 0);
        public bool IsPartlyPaid => Invoice is not null && !IsVoid && !IsPaid && Invoice.TotalPaid > 0;
        public bool IsOverdue => Invoice is not null && !IsVoid && !IsPaid && Invoice.IsOverdue;

        public bool ShowSeller => Seller?.HasDetails == true;

        // ── Who it is for (053 rule, as QuoteView) ────────────────────
        public string CustomerHeading
            => Invoice is null ? string.Empty
             : !string.IsNullOrWhiteSpace(Invoice.CompanyName) ? Invoice.CompanyName.Trim()
             : Invoice.ContactName?.Trim() ?? string.Empty;

        public string? CustomerSubheading
            => Invoice is null || string.IsNullOrWhiteSpace(Invoice.CompanyName) || string.IsNullOrWhiteSpace(Invoice.ContactName)
                ? null
                : Invoice.ContactName.Trim();

        public string TaxLabel => string.IsNullOrWhiteSpace(Seller?.TaxLabel) ? "Tax" : Seller!.TaxLabel;

        // ── Money in the invoice's own currency ───────────────────────
        public string CurrencySymbol => string.IsNullOrWhiteSpace(Invoice?.Currency)
            ? string.Empty
            : CurrencyConfiguration.GetCurrencySymbol(Invoice!.Currency);

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CultureInfo> _cultureCache = new();

        private static CultureInfo CultureForCurrency(string? code)
        {
            if (string.IsNullOrWhiteSpace(code)) return CultureInfo.InvariantCulture;

            return _cultureCache.GetOrAdd(code, c => c switch
            {
                "INR" => new CultureInfo("en-IN"),   // lakh grouping
                "PHP" => new CultureInfo("en-PH"),
                "THB" => new CultureInfo("th-TH"),
                "AED" => new CultureInfo("en-AE"),
                "USD" => new CultureInfo("en-US"),
                "EUR" => new CultureInfo("en-IE"),
                "GBP" => new CultureInfo("en-GB"),
                _     => CultureInfo.InvariantCulture
            });
        }

        public string FormatCurrency(decimal amount)
            => $"{CurrencySymbol}{amount.ToString("N2", CultureForCurrency(Invoice?.Currency))}";

        /// <summary>A tax rate that is not rounded — InvoiceLineDto.TaxRate is a FRACTION.</summary>
        public string FormatTaxRate(decimal fraction)
            => (fraction * 100m).ToString("0.##", CultureForCurrency(Invoice?.Currency)) + "%";

        /// <summary>Dates with the Gregorian calendar whatever the culture — a Thai customer still reads 2026, not 2569.</summary>
        public static string FormatDate(DateTime d) => d.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);

        public string FormatLineDiscount(InvoiceLineDto line)
            => !string.IsNullOrWhiteSpace(line.DiscountLabel) ? line.DiscountLabel!
             : line.LineDiscount > 0 ? "-" + FormatCurrency(line.LineDiscount)
             : "—";

        public bool HasAnyLineDiscount =>
            Invoice is not null && Invoice.Lines.Any(l => l.LineDiscount > 0 || l.DiscountPercent.HasValue);

        public bool HasAnyLineTaxCode =>
            Invoice is not null && TaxCodes.AnyPresent(Invoice.Lines.Select(l => l.TaxCode));

        public string TaxCodeLabel => TaxCodes.ColumnHeaderFor(Invoice?.Currency);

        public string LineDetails(InvoiceLineDto line)
            => DocumentFieldEntry.JoinForLine(line.DocumentFields, CultureForCurrency(Invoice?.Currency), "dd MMM yyyy");

        public IReadOnlyList<(string Label, string Value)> PrintedDetails =>
            Invoice?.DocumentFields is { Count: > 0 } fields
                ? fields.Select(f => (f.Label, f.Format(CultureForCurrency(Invoice.Currency), "dd MMM yyyy"))).ToList()
                : new List<(string Label, string Value)>();

        /// <summary>Days until due (negative when overdue), or null with no due date.</summary>
        public int? DaysToDue => Invoice?.DueDateUtc is DateTime due
            ? (int)Math.Floor((due.Date - DateTime.UtcNow.Date).TotalDays)
            : null;

        private string MaskedToken =>
            string.IsNullOrWhiteSpace(Token) || Token.Length < 8 ? "****" : "****" + Token[^4..];

        // ── GET ───────────────────────────────────────────────────────
        public async Task<IActionResult> OnGetAsync()
        {
            if (string.IsNullOrWhiteSpace(Token) || Token.Length > 64)
            {
                Response.StatusCode = 404;
                return Page();      // the page's own "link not valid" state
            }

            Data = await _invoices.GetByTokenAsync(Token);
            if (Data is null)
            {
                Response.StatusCode = 404;
                _logger.LogInformation("Invoice link {Token} opened but not found", MaskedToken);
            }

            return Page();
        }

        // ── GET ?handler=Pdf — the PDF as it was emailed ──────────────
        public async Task<IActionResult> OnGetPdfAsync()
        {
            if (string.IsNullOrWhiteSpace(Token) || Token.Length > 64)
                return NotFound();

            try
            {
                var bytes = await _invoices.GetPublicPdfAsync(Token);
                if (bytes is not null)
                {
                    // The name comes from the invoice, not the request.
                    var meta = await _invoices.GetByTokenAsync(Token);
                    var name = string.IsNullOrWhiteSpace(meta?.PdfFileName) ? "invoice.pdf" : meta!.PdfFileName!;
                    return File(bytes, "application/pdf", name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to download the PDF for invoice link {Token}", MaskedToken);
            }

            // Back to the page, which says the PDF is not available.
            Data = await _invoices.GetByTokenAsync(Token);
            if (Data is null)
            {
                Response.StatusCode = 404;
                return Page();
            }

            PdfFailed = true;
            return Page();
        }
    }
}
