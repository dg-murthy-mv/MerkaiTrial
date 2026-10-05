// =====================================================================
// QuoteMilestoneService.cs
// Location: MerkaiTrial.Admin.Web/Services/Quotes/QuoteMilestoneService.cs
//
// NEW FILE (071).
//
// Thin wrapper over /api/quotes/{id}/billing-schedule, in the same shape
// as ProductCategoryService from 068. No tenantId parameters anywhere:
// that controller reads the workspace from the signed token.
//
// THE READ DEGRADES, THE WRITE DOES NOT — and here the asymmetry is
// worth stating, because the read degrades to something that LOOKS like
// a valid answer.
//
//   GetAsync returns an empty BillingScheduleDto if the call fails. An
//   empty schedule means "invoice the whole quote once", which is a
//   perfectly normal state — so a quote page that could not reach the
//   API would show a quote with no payment terms rather than an error.
//
//   That is the right trade for the QUOTE PAGE (one panel missing beats
//   a dead page), and the wrong trade for anything that decides money.
//   So the returned object carries LoadFailed, and the invoice-create
//   page refuses to raise an invoice while it is true, rather than
//   quietly billing the whole amount against a quote that was split
//   into stages. That is the one mistake this file exists to prevent.
//
//   SaveAsync does not catch anything. IApiService turns a 400 with
//   { "error": "..." } into an InvalidOperationException carrying the
//   message, and those messages are written for the person: "The stages
//   come to 90,000.00 of 100,000.00 — 10,000.00 (10%) is unaccounted
//   for." Catching that here would replace it with silence and a page
//   that looks like it saved.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Core;
using MerkaiTrial.Application.DTOs;

namespace MerkaiTrial.Admin.Web.Services.Quotes
{
    /// <summary>
    /// A schedule read, plus whether the read actually worked. See the
    /// header: "no schedule" and "could not load the schedule" look
    /// identical in a BillingScheduleDto, and one of them must never be
    /// allowed to become an invoice.
    /// </summary>
    public sealed class BillingScheduleResult
    {
        public BillingScheduleDto Schedule { get; init; } = new();

        /// <summary>
        /// True when the API call failed. The quote page carries on and
        /// shows a quiet warning; the invoice-create page refuses to
        /// create anything.
        /// </summary>
        public bool LoadFailed { get; init; }

        public bool HasSchedule => !LoadFailed && Schedule.HasSchedule;
    }

    public interface IQuoteMilestoneService
    {
        /// <summary>
        /// The schedule for a quote, with every stage's computed amount
        /// and what has been invoiced against it. Never throws, never
        /// returns null — check LoadFailed before acting on it.
        /// </summary>
        Task<BillingScheduleResult> GetAsync(Guid quoteId);

        /// <summary>
        /// Replace the schedule. An EMPTY list removes it and the quote
        /// goes back to being invoiced once, for the whole amount; see
        /// the null-vs-empty note on SaveBillingScheduleDto.
        ///
        /// Throws InvalidOperationException carrying a message written
        /// for the person. Let it reach the page.
        /// </summary>
        Task<BillingScheduleDto> SaveAsync(Guid quoteId, List<QuoteMilestoneDto> milestones);
    }

    public class QuoteMilestoneService : IQuoteMilestoneService
    {
        private readonly IApiService _apiService;
        private readonly ILogger<QuoteMilestoneService> _logger;

        public QuoteMilestoneService(
            IApiService apiService,
            ILogger<QuoteMilestoneService> logger)
        {
            _apiService = apiService;
            _logger = logger;
        }

        public async Task<BillingScheduleResult> GetAsync(Guid quoteId)
        {
            try
            {
                var dto = await _apiService.GetAsync<BillingScheduleDto>(
                    $"api/quotes/{quoteId}/billing-schedule");

                // A null body with a 200 should not happen, but it is not
                // the same thing as a failure either — treat it as "no
                // schedule" and say nothing.
                return new BillingScheduleResult
                {
                    Schedule = dto ?? new BillingScheduleDto { QuoteId = quoteId },
                    LoadFailed = false
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load the billing schedule for quote {QuoteId}", quoteId);

                return new BillingScheduleResult
                {
                    Schedule = new BillingScheduleDto { QuoteId = quoteId },
                    LoadFailed = true
                };
            }
        }

        public async Task<BillingScheduleDto> SaveAsync(Guid quoteId, List<QuoteMilestoneDto> milestones)
            => await _apiService.PutAsync<BillingScheduleDto>(
                   $"api/quotes/{quoteId}/billing-schedule",
                   new SaveBillingScheduleDto
                   {
                       QuoteId = quoteId,
                       // NEVER null from this service. Null means "leave
                       // the schedule alone", and a page that posted a
                       // schedule form would then silently save nothing.
                       // The empty list — "remove the schedule" — is a
                       // real instruction the editor sends on purpose.
                       Milestones = milestones ?? new List<QuoteMilestoneDto>()
                   })
               ?? new BillingScheduleDto { QuoteId = quoteId };
    }
}
