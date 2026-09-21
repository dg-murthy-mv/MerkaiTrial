// =====================================================================
// Pipeline/Index.cshtml.cs
// Location: MerkaiTrial.Admin.Web/Pages/Pipeline/Index.cshtml.cs
//
// COMPLETE FILE — replaces the existing one.
//
// CHANGES (019 — transition rules):
//   ✅ Loads the tenant's transition rules alongside the stages, so the
//      board can ask for a lost reason or a reopen reason BEFORE it posts
//      rather than posting, being refused, and asking afterwards.
//   ✅ OnPostUpdateStageAsync takes those two reasons and goes through
//      the new IDealStageService, which can carry them. IDealService is
//      untouched and still used everywhere else.
//   ✅ The refusal message from the server is passed straight back to the
//      browser. It is written for a salesperson ("This deal isn't ready
//      for Closed Won yet — it needs an accepted quote"), and replacing
//      it with "Failed to update deal stage" was throwing away the only
//      useful part of the response.
//   ✅ StageName / StageCategoryOf helpers so the table view stops
//      matching hard-coded stage keys.
//
// CHANGES (017):
//   ✅ DealQuotaUsed — every deal in the workspace (GetDealsResponse.QuotaUsed),
//      for the Deal Quota bar and the "Deal Limit Reached" button. They
//      used TotalDeals, which is only the deals THIS user can see, so a rep
//      on Own saw "0 / 500 used" while the Dashboard said 8 / 500 — and
//      could never hit the limit button however full the workspace was.
//
// CHANGES (deals visibility round):
//   ✅ Loads deals with pageSize 500. It used the default 20, so the board
//      silently stopped at 20 deals and every stage total / win rate on
//      the page was computed from those 20. The API now clamps at 500
//      instead of resetting anything over 100 back to 20.
//   ✅ The board shows only deals this user can see (Own / Team / All) —
//      the API does it; nothing to change here.
//   ✅ Stage drag on a deal outside scope → the API returns 404.
//
// EARLIER CHANGES:
//   ✅ Removed duplicate ICurrentTenantService (_currentTenantService)
//      — was injected twice as both _tenantService and _currentTenantService
//      — kept _tenantService only, removed _currentTenantService everywhere
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Deals;
using MerkaiTrial.Admin.Web.Services.Pipeline;
using MerkaiTrial.Admin.Web.Services.Quotes;
using MerkaiTrial.Admin.Web.Services.Users;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Commands.PipelineStages;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Tenants;
using MerkaiTrial.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.Admin.Web.Pages.Pipeline
{
    public class IndexModel : AuthorizedPageModel
    {
        private readonly IDealService _dealService;
        private readonly IDealStageService _dealStageService;          // ✅ 019
        private readonly IUserService _userService;
        private readonly IQuoteService _quoteService;
        private readonly ICurrentUserService _currentUserService;
        private readonly ICurrentTenantService _tenantService;          // ✅ ONE service only
        private readonly ILogger<IndexModel> _logger;
        private readonly IPipelineStageService _stageService;
        private readonly IPipelineRuleService _ruleService;             // ✅ 019

        protected override string ModuleName => Modules.Deals;

        public IndexModel(
            IDealService dealService,
            IDealStageService dealStageService,                          // ✅ 019
            IUserService userService,
            IQuoteService quoteService,
            ICurrentUserService currentUserService,
            ICurrentTenantService tenantService,                        // ✅ ONE param only
            IAuthorizationService authorizationService,
            ILogger<IndexModel> logger,
            IPipelineStageService stageService,
            IPipelineRuleService ruleService)                           // ✅ 019
            : base(authorizationService, currentUserService, logger)
        {
            _dealService = dealService;
            _dealStageService = dealStageService;
            _userService = userService;
            _quoteService = quoteService;
            _currentUserService = currentUserService;
            _tenantService = tenantService;
            _logger = logger;
            _stageService = stageService;
            _ruleService = ruleService;
        }

        // ── View Properties ────────────────────────────────────────────
        public List<DealListItem> Deals { get; set; } = new();
        public List<SalesTeamMemberDto> SalesTeam { get; set; } = new();
        public Dictionary<Guid, QuoteStatusInfo> DealQuoteStatus { get; set; } = new();

        // ── Tenant Currency ────────────────────────────────────────────
        public string TenantCurrencySymbol { get; set; } = string.Empty;
        public string TenantCurrencyCode { get; set; } = string.Empty;
        public List<PipelineStageDto> Stages { get; private set; } = new();

        /// <summary>
        /// The tenant's transition rules. Null only when the call failed —
        /// the board still works, it just posts without pre-asking and
        /// lets the server explain.
        /// </summary>
        public PipelineRulesDto? Rules { get; private set; }

        // ── Stats ──────────────────────────────────────────────────────
        public int TotalDeals => Deals.Count;

        /// <summary>Every deal in the workspace — for the plan quota bar and the New Deal limit.</summary>
        public int DealQuotaUsed { get; private set; }
        public decimal TotalValue => Deals.Sum(d => d.ExpectedValue);

        private HashSet<string> KeysWith(StageCategory c) =>
            Stages.Where(s => s.Category == c).Select(s => s.Key).ToHashSet();

        public decimal WeightedValue
        {
            get
            {
                var open = KeysWith(StageCategory.Open);
                return Deals.Where(d => open.Contains(d.Stage))
                            .Sum(d => d.ExpectedValue * d.Probability / 100m);
            }
        }

        public int ClosedWonCount => Deals.Count(d => KeysWith(StageCategory.Won).Contains(d.Stage));
        public int ClosedLostCount => Deals.Count(d => KeysWith(StageCategory.Lost).Contains(d.Stage));
        public int WinRate
        {
            get
            {
                var closed = ClosedWonCount + ClosedLostCount;
                return closed == 0 ? 0 : (int)Math.Round(ClosedWonCount * 100m / closed);
            }
        }
        public decimal AvgDealSize => TotalDeals > 0 ? TotalValue / TotalDeals : 0;

        // ── Filters ────────────────────────────────────────────────────
        [BindProperty(SupportsGet = true)] public string? OwnerFilter { get; set; }
        [BindProperty(SupportsGet = true)] public string? StageFilter { get; set; }
        [BindProperty(SupportsGet = true)] public string? SearchTerm { get; set; }

        [TempData] public string? ErrorMessage { get; set; }
        [TempData] public string? SuccessMessage { get; set; }

        // ── GET ────────────────────────────────────────────────────────
        public async Task<IActionResult> OnGetAsync()
        {
            var check = await ValidatePermissionAsync(Actions.Read);
            if (check != null) return check;

            await InitializePermissionsAsync();

            try
            {
                var tenantId = _currentUserService.GetCurrentTenantId();

                TenantCurrencyCode = _tenantService.GetCurrencyCode();
                TenantCurrencySymbol = _tenantService.GetCurrencySymbol();

                _logger.LogInformation(
                    "Loading pipeline for TenantId={TenantId} Currency={Currency}",
                    tenantId, TenantCurrencyCode);

                var dealsTask = _dealService.GetAllAsync(tenantId, stage: StageFilter,
                                        search: SearchTerm, ownerUserId: OwnerFilter,
                                        pageSize: 500);
                var salesTeamTask = _userService.GetSalesTeamAsync(tenantId);
                var stagesTask = _stageService.GetAsync(activeOnly: true);
                var rulesTask = _ruleService.GetAsync();

                await Task.WhenAll(dealsTask, salesTeamTask, stagesTask, rulesTask);

                var dealsResponse = await dealsTask;
                Deals = dealsResponse.Items;
                DealQuotaUsed = dealsResponse.QuotaUsed;
                SalesTeam = await salesTeamTask;
                Stages = await stagesTask;
                Rules = await rulesTask;

                await LoadQuoteStatusForDealsAsync(tenantId);

                _logger.LogInformation("Loaded {Count} deals", Deals.Count);

                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load pipeline");
                ErrorMessage = "Failed to load pipeline. Please try again.";
                Deals = new();
                SalesTeam = new();
                DealQuoteStatus = new();
                return Page();
            }
        }

        // ── POST: Update Stage (Kanban drag & drop) ────────────────────

        /// <summary>
        /// 019: carries the reason the move needs. The board asks for it
        /// first when its copy of the rules says one is wanted, but the
        /// server is the authority — if the board's copy is stale, the
        /// refusal below explains exactly what is missing.
        /// </summary>
        public async Task<IActionResult> OnPostUpdateStageAsync(
            Guid dealId, string stage, string? lostReason, string? reopenReason)
        {
            if (!await CanUpdateAsync())
                return new JsonResult(new { success = false, error = "You do not have permission to update deal stages." }) { StatusCode = 403 };

            try
            {
                await _dealStageService.MoveAsync(dealId, stage, lostReason, reopenReason);
                return new JsonResult(new { success = true });
            }
            catch (KeyNotFoundException)
            {
                return new JsonResult(new { success = false, error = "Deal not found." }) { StatusCode = 404 };
            }
            // IApiService turns a 400 or 403 { "error": "..." } body into
            // this, carrying the server's own wording. That wording is
            // written for the rep and is the whole point of the round —
            // showing it beats "Failed to update deal stage."
            catch (InvalidOperationException ex)
            {
                _logger.LogInformation(
                    "Stage move refused for deal {DealId} -> {Stage}: {Message}", dealId, stage, ex.Message);

                return new JsonResult(new { success = false, error = ex.Message, refused = true })
                { StatusCode = 400 };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update stage for deal {DealId}", dealId);
                return new JsonResult(new { success = false, error = "Something went wrong moving that deal. Please try again." })
                { StatusCode = 500 };
            }
        }

        // ── ✅ VIEW HELPERS: tenant-aware money ────────────────────────
        // The view previously rendered every figure as
        //   @Model.TenantCurrencySymbol@value.ToString("N0")
        // "N0" with no culture uses the SERVER's CurrentCulture. On an en-IN
        // dev box that put Indian lakh grouping on every tenant — Sathorn
        // (Thailand) showed ฿9,15,000 instead of ฿915,000.
        public string FormatCurrency(decimal amount) => _tenantService.FormatCurrency(amount);
        public string FormatCurrency(decimal amount, int decimals) => _tenantService.FormatCurrency(amount, decimals);

        // ── Kanban Helpers ─────────────────────────────────────────────
        public List<DealListItem> GetDealsByStage(string stage) => Deals.Where(d => d.Stage == stage).ToList();
        public int GetStageCount(string stage) => Deals.Count(d => d.Stage == stage);
        public decimal GetStageValue(string stage) => Deals.Where(d => d.Stage == stage).Sum(d => d.ExpectedValue);
        public QuoteStatusInfo? GetQuoteStatus(Guid dealId) => DealQuoteStatus.TryGetValue(dealId, out var s) ? s : null;

        // ── ✅ 019: stage lookups for the table view ───────────────────
        // The table used to switch on the literal strings "Discovery",
        // "ClosedWon" and so on, so a tenant who renamed a stage saw their
        // own name on the board and ours in the table.

        public string StageName(string? key) =>
            string.IsNullOrEmpty(key) ? "—"
            : (Stages.FirstOrDefault(s => s.Key == key)?.Name ?? key);

        public StageCategory StageCategoryOf(string? key) =>
            Stages.FirstOrDefault(s => s.Key == key)?.Category ?? StageCategory.Open;

        public bool IsClosedStage(string? key) =>
            StageCategoryOf(key) is StageCategory.Won or StageCategory.Lost;

        public string StageBadgeClass(string? key) => StageCategoryOf(key) switch
        {
            StageCategory.Won => "bg-success",
            StageCategory.Lost => "bg-danger",
            _ => "bg-secondary"
        };

        /// <summary>
        /// Does moving INTO this stage need a lost reason? Read from the
        /// tenant's rules so the board asks before posting.
        /// </summary>
        public bool StageNeedsLostReason(string key) =>
            Rules?.Stages.FirstOrDefault(s => s.Key == key) is { RequiresLostReason: true, Category: StageCategory.Lost };

        /// <summary>Does moving OUT of a closed stage need a reason?</summary>
        public bool ReopenNeedsReason => Rules?.ReopenRequiresReason ?? false;

        // ── Load Quote Status for Kanban Cards ─────────────────────────
        private async Task LoadQuoteStatusForDealsAsync(Guid tenantId)
        {
            try
            {
                var allQuotes = await _quoteService.GetAllAsync(tenantId);

                if (allQuotes == null || !allQuotes.Any())
                {
                    DealQuoteStatus = new();
                    return;
                }

                DealQuoteStatus = allQuotes
                    .Where(q => q.DealId.HasValue && q.DealId.Value != Guid.Empty)
                    .GroupBy(q => q.DealId!.Value)
                    .ToDictionary(
                        g => g.Key,
                        g =>
                        {
                            var ordered = g.OrderByDescending(q => q.CreatedAtUtc).ToList();
                            var latest = ordered.First();
                            var accepted = g.FirstOrDefault(q =>
                                string.Equals(q.Status, "Accepted", StringComparison.OrdinalIgnoreCase));

                            return new QuoteStatusInfo
                            {
                                HasAcceptedQuote = accepted != null,
                                LatestQuoteNumber = latest.Number,
                                LatestQuoteStatus = latest.Status,
                                AcceptedQuoteId = accepted?.Id
                            };
                        });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load quote status for deals");
                DealQuoteStatus = new();
            }
        }
    }

    public class QuoteStatusInfo
    {
        public bool HasAcceptedQuote { get; set; }
        public string LatestQuoteNumber { get; set; } = string.Empty;
        public string LatestQuoteStatus { get; set; } = string.Empty;
        public Guid? AcceptedQuoteId { get; set; }
    }
}
