// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Public/QuoteView.cshtml.cs
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Quotes;
using MerkaiTrial.Application.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Globalization;

namespace MerkaiTrial.Admin.Web.Pages.Public
{
    [AllowAnonymous]
    public class QuoteViewModel : PageModel
    {
        private readonly IQuoteService          _quoteService;
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

        // ── ✅ TENANT-AWARE MONEY, WITHOUT A TENANT CONTEXT ────────────────
        // This page is [AllowAnonymous] — the customer following a public quote
        // link has no session, no ViewAs cookie and no tenant claims, so
        // ICurrentTenantService cannot resolve a country here and the
        // AuthorizedPageModel.FormatCurrency helper is unavailable (this is a
        // plain PageModel by design).
        //
        // The view previously did @Model.CurrencySymbol@x.ToString("N2") with no
        // culture, which falls back to the SERVER's CurrentCulture — so an Indian
        // dev box rendered Thai and UAE customer quotes with lakh grouping
        // (฿6,41,037.00). Worse than the internal pages: this one is seen by the
        // customer.
        //
        // Fix: derive the culture from the quote's own currency code, the same
        // mapping InvoicePdfService/QuotePdfService use. The quote carries its
        // currency, so no tenant lookup is needed.
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

        // ── GET ───────────────────────────────────────────────────────
        public async Task<IActionResult> OnGetAsync(CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(Token))
                return NotFound();

            Quote = await _quoteService.GetByTokenAsync(Token);

            if (Quote == null)
                return NotFound();

            IsExpired  = Quote.ExpiresAtUtc < DateTime.UtcNow
                         && Quote.Status is "Draft" or "Sent" or "Viewed";
            IsAccepted = Quote.Status == "Accepted";
            IsRejected = Quote.Status == "Rejected";

            return Page();
        }

        // ── POST: Customer Accepts ────────────────────────────────────
        public async Task<IActionResult> OnPostAcceptAsync(CancellationToken ct)
        {
            try
            {
                var result = await _quoteService.UpdateStatusByTokenAsync(Token, "Accepted");
                if (!result) return NotFound();

                // ✅ Explicit page path clears ?handler=Accept from URL
                return RedirectToPage("/Public/QuoteView", new { Token });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to accept quote via token {Token}", Token);
                return RedirectToPage("/Public/QuoteView", new { Token });
            }
        }


        // ── POST: Customer Declines ───────────────────────────────────
        public async Task<IActionResult> OnPostDeclineAsync(CancellationToken ct)
        {
            try
            {
                var result = await _quoteService.UpdateStatusByTokenAsync(Token, "Rejected");
                if (!result) return NotFound();

                // ✅ Same fix
                return RedirectToPage("/Public/QuoteView", new { Token });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to decline quote via token {Token}", Token);
                return RedirectToPage("/Public/QuoteView", new { Token });
            }
        }
    }
}
