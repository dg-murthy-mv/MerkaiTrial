// =====================================================================
// FILE: MerkaiTrial.Application/DTOs/SummaryDtos.cs
//
// NEW (107-update — performance). Small answers that replace "load
// everything and count it in the browser's server":
//
//   DealSummaryDto       the dashboard's deal numbers, worked out by the
//                        database over EVERY deal the person can see. The
//                        dashboard used to load the newest 500 deals and
//                        count those — slow with 20,000 deals, and WRONG:
//                        deal 501 onwards was simply missing from Active,
//                        Won, Lost and the pipeline value.
//
//   QuoteDealStatusDto   per deal: its latest quote's number and status,
//                        and whether any quote is accepted. The pipeline
//                        board used to load EVERY quote in the workspace
//                        (with their deals, contacts, companies and lines)
//                        just to draw these badges on the deals it shows.
// =====================================================================

namespace MerkaiTrial.Application.DTOs
{
    /// <summary>
    /// The dashboard's deal panels. Counted by stage CATEGORY (Open / Won /
    /// Lost) within the chosen period, by the same rule the dashboard
    /// always used — see GetDealSummaryHandler.
    /// </summary>
    public sealed record DealSummaryDto(
        int ActiveDeals,
        int DealsWon,
        int DealsLost,
        decimal PipelineValue,
        decimal WonValue,
        int UndatedDeals,
        int GhostStageDeals,
        int QuotaUsed,
        List<DealListItem> TopDeals);

    /// <summary>One deal's quote badge on the pipeline board.</summary>
    public sealed record QuoteDealStatusDto(
        Guid DealId,
        string LatestQuoteNumber,
        string LatestQuoteStatus,
        bool HasAcceptedQuote,
        Guid? AcceptedQuoteId);

    /// <summary>POST /api/quotes/deal-status — the deals on the page being drawn.</summary>
    public sealed class QuoteDealStatusRequest
    {
        public List<Guid> DealIds { get; set; } = new();
    }
}
