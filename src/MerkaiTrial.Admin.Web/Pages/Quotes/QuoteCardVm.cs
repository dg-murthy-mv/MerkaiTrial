// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Quotes/QuoteCardVm.cs
//
// NEW FILE (028). What _QuoteCard.cshtml needs to draw one quote on the
// board.
//
// WHY THIS EXISTS AT ALL
//   The old Index.cshtml had the same card markup copied out six times,
//   once per column — roughly 250 lines of duplication. It had already
//   drifted: the Draft column carried an Edit button the others didn't,
//   the Sent column showed an expiry date where the rest showed an item
//   count, and three statuses had no column at all. One partial, one
//   card, one place to change it.
//
// A typed record rather than ViewData, so a renamed field is a build
// error instead of a blank box at run time. Same pattern as RuleEditorVm
// in round 027.
// =====================================================================

using MerkaiTrial.Application.DTOs;

namespace MerkaiTrial.Admin.Web.Pages.Quotes;

/// <param name="Quote">The quote to draw.</param>
/// <param name="Page">
/// The page model, for the tenant-aware formatters and the permission
/// flags. Passing it keeps currency, dates and expiry maths in one place
/// — a card must never work any of that out itself.
/// </param>
public record QuoteCardVm(QuoteListItem Quote, IndexModel Page)
{
    public IndexModel.StatusMeta Meta => IndexModel.Meta(Quote.Status);

    /// <summary>Only a draft or a revision is worth an inline Edit shortcut.</summary>
    public bool CanEditInline =>
        Page.CanUpdate &&
        (string.Equals(Quote.Status, "Draft", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(Quote.Status, "Revised", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Expiry only matters while the quote is actually out with the
    /// customer — and only when one was set. A quote saved without an
    /// expiry carries default(DateTime), which would read as expired
    /// several centuries ago.
    /// </summary>
    public bool ShowExpiry =>
        IndexModel.IsLive(Quote.Status) && IndexModel.HasExpiry(Quote.ExpiresAtUtc);

    public bool IsExpiringSoon =>
        ShowExpiry && Page.DaysLeft(Quote.ExpiresAtUtc) is >= 0 and <= 3;

    public bool HasExpired =>
        ShowExpiry && Page.DaysLeft(Quote.ExpiresAtUtc) < 0;

    public string ExpiryNote => Page.ExpiryNote(Quote.ExpiresAtUtc);

    /// <summary>Lower-cased haystack for the client-side search box.</summary>
    public string SearchText =>
        $"{Quote.Number} {Quote.CompanyName} {Quote.DealTitle} {Meta.Label}".ToLowerInvariant();
}
