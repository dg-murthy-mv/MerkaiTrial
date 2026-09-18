// =====================================================================
// Pipeline/Index.cshtml.cs
// Location: MerkaiTrial.Admin.Web/Pages/Pipeline/Index.cshtml.cs
//
// CHANGES:
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
        private readonly IUserService _userService;
        private readonly IQuoteService _quoteService;
        private readonly ICurrentUserService _currentUserService;
        private readonly ICurrentTenantService _tenantService;          // ✅ ONE service only
        private readonly ILogger<IndexModel> _logger;
        private readonly IPipelineStageService _stageService;
        protected override string ModuleName => Modules.Deals;

        public IndexModel(
            IDealService dealService,
            IUserService userService,
            IQuoteService quoteService,
            ICurrentUserService currentUserService,
            ICurrentTenantService tenantService,                        // ✅ ONE param only
            IAuthorizationService authorizationService,
            ILogger<IndexModel> logger, IPipelineStageService stageService)
            : base(authorizationService, currentUserService, logger)
        {
            _dealService = dealService;
            _userService = userService;
            _quoteService = quoteService;
            _currentUserService = currentUserService;
            _tenantService = tenantService;
            _logger = logger;
            _stageService = stageService;
        }

        // ── View Properties ────────────────────────────────────────────
        public List<DealListItem> Deals { get; set; } = new();
        public List<SalesTeamMemberDto> SalesTeam { get; set; } = new();
        public Dictionary<Guid, QuoteStatusInfo> DealQuoteStatus { get; set; } = new();

        // ── Tenant Currency ────────────────────────────────────────────
        public string TenantCurrencySymbol { get; set; } = string.Empty;
        public string TenantCurrencyCode { get; set; } = string.Empty;
        public List<PipelineStageDto> Stages { get; private set; } = new();

        // ── Stats ──────────────────────────────────────────────────────
        public int TotalDeals => Deals.Count;
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
                                        search: SearchTerm, ownerUserId: OwnerFilter);
                var salesTeamTask = _userService.GetSalesTeamAsync(tenantId);
                var stagesTask = _stageService.GetAsync(activeOnly: true);

                await Task.WhenAll(dealsTask, salesTeamTask, stagesTask);

                Deals = (await dealsTask).Items;
                SalesTeam = await salesTeamTask;
                Stages = await stagesTask;

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
        public async Task<IActionResult> OnPostUpdateStageAsync(Guid dealId, string stage)
        {
            if (!await CanUpdateAsync())
                return new JsonResult(new { success = false, error = "You do not have permission to update deal stages." }) { StatusCode = 403 };

            try
            {
                var tenantId = _currentUserService.GetCurrentTenantId();
                await _dealService.UpdateStageAsync(tenantId, dealId, stage);
                return new JsonResult(new { success = true });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update stage for deal {DealId}", dealId);
                return new JsonResult(new { success = false, error = ex.Message }) { StatusCode = 500 };
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
