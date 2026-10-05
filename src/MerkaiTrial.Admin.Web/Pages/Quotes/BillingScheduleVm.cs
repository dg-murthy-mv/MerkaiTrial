// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Quotes/BillingScheduleVm.cs
//
// NEW FILE (071).
//
// Everything _BillingSchedule.cshtml needs. Built in the page and passed
// to the partial; the partial reads nothing off the page model, so the
// panel cannot pick up a dependency on which page is drawing it.
//
// Same arrangement as PriceGridVm (069), BundleGridVm (070) and
// QuoteItemsEditorVm (029), and for the reason that last file sets out:
// two copies of one editor drift, and one of the copies ends up with a
// hole the other does not have.
//
// ─────────────────────────────────────────────────────────────────────
// WHY THERE IS A Func<decimal, string> ON A VIEW MODEL
//
//   Because money on this panel is in the QUOTE's currency, not the
//   workspace's, and the page already owns that rule — DetailModel.Money
//   appends the ISO code when the quote is in something other than the
//   workspace currency, and uses invariant grouping for it, because
//   Indian lakh grouping on a dollar figure reads as a mistake.
//
//   Re-deriving that here would be a second copy of a rule that round
//   029 was specifically written to stop having two of. Passing the
//   page's own helper in is the smaller evil, and a view model is
//   allowed to hold a delegate — it is not a DTO and nothing serialises
//   it.
// ─────────────────────────────────────────────────────────────────────
// =====================================================================

using System.Text.Json;
using MerkaiTrial.Admin.Web.Services.Quotes;
using MerkaiTrial.Application.DTOs;

namespace MerkaiTrial.Admin.Web.Pages.Quotes
{
    public sealed class BillingScheduleVm
    {
        public Guid QuoteId { get; init; }

        /// <summary>
        /// The schedule as read, INCLUDING whether the read worked. Both
        /// matter on screen: a failed read and "no schedule" look
        /// identical in the DTO, and the panel has to say which one
        /// happened rather than quietly implying there are no payment
        /// terms.
        /// </summary>
        public BillingScheduleResult Billing { get; init; } = new();

        /// <summary>The quote's own currency symbol, for the editor's live totals.</summary>
        public string CurrencySymbol { get; init; } = string.Empty;

        /// <summary>
        /// The quote's grand total — what every share is a share of, and
        /// what the editor's running total has to reach.
        /// </summary>
        public decimal QuoteTotal { get; init; }

        /// <summary>
        /// Formats an amount in the QUOTE's currency. See the header for
        /// why this arrives as a delegate.
        /// </summary>
        public Func<decimal, string> Money { get; init; } = v => v.ToString("N2");

        /// <summary>Formats a date in the tenant's timezone — the page's own helper.</summary>
        public Func<DateTime, string> Date { get; init; } = d => d.ToString("dd MMM yyyy");

        /// <summary>
        /// True when the Edit button shows. Needs quotes.update; the API
        /// checks it again, because a hidden button is not a control.
        /// </summary>
        public bool CanEdit { get; init; }

        /// <summary>
        /// True when the per-stage "Create invoice" buttons show. This is
        /// a CROSS-MODULE check — raising an invoice from the quote page
        /// needs invoices.create as well as quotes.update — and it is the
        /// same test OnPostCreateInvoiceAsync makes before it does
        /// anything.
        /// </summary>
        public bool CanCreateInvoice { get; init; }

        /// <summary>
        /// Every invoice on this quote, void ones included, so a stage can
        /// link to the invoice that covers it and the void history is
        /// visible instead of looking like nothing ever happened.
        /// </summary>
        public List<InvoiceListItem> Invoices { get; init; } = new();

        // ── For the editor ────────────────────────────────────────────

        /// <summary>
        /// The name of the handler parameter the editor posts into.
        /// Bound by NAME rather than with asp-for, because asp-for would
        /// derive the name from THIS view model and post something no
        /// handler takes — the 069 mistake, written down so it is not
        /// made a third time.
        /// </summary>
        public string ScheduleJsonFieldName { get; init; } = "scheduleJson";

        /// <summary>
        /// The existing stages as a JSON array: the hidden field's
        /// initial value AND the editor script's starting rows, from ONE
        /// serialisation so the two cannot begin out of step.
        ///
        /// NEVER "[]" when a schedule exists. An "[]" default would post
        /// "remove the schedule" if the editor's script failed to run —
        /// the exact bug 069 caught in the price grid before it shipped.
        /// The handler also refuses a payload it cannot parse rather than
        /// treating it as empty; that is the backstop, and seeding
        /// properly is the fix.
        /// </summary>
        public string ScheduleJson =>
            JsonSerializer.Serialize(
                Billing.Schedule.Rows.Select(r => new
                {
                    id = r.Id,
                    name = r.Name,
                    percent = r.Percent,
                    fixedAmount = r.FixedAmount,
                    dueCondition = r.DueCondition,
                    dueDate = r.DueDateUtc?.ToString("yyyy-MM-dd"),

                    // The invoice number, so a row that has already been
                    // billed says so in the editor. There is NO per-row
                    // lock flag here on purpose: the lock is a property
                    // of the whole schedule (see
                    // BillingScheduleDto.AmountsLocked), because changing
                    // ANY row's share re-cuts the remainder on the last
                    // one. A per-row flag would read as though the other
                    // rows were still editable.
                    invoiceNumber = r.InvoiceNumber
                }));

        /// <summary>
        /// The two-stage starting point the editor offers on an empty
        /// schedule — "half up front, half on delivery", the commonest
        /// arrangement in all four of this product's markets. Serialised
        /// server-side so the wording lives beside the rest of the
        /// defaults in QuoteMilestones and not in a script.
        /// </summary>
        public string SuggestedJson { get; init; } = "[]";
    }
}
