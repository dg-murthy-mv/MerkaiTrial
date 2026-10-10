// =====================================================================
// Pipeline/Index.cshtml.cs
// Location: MerkaiTrial.Admin.Web/Pages/Pipeline/Index.cshtml.cs
//
// COMPLETE FILE — replaces the existing one.
//
// CHANGES (077 — custom fields on deals):
//   ✅ Deal custom fields marked "Show on the list" (up to four) are
//      columns in the TABLE view and short lines on each BOARD card, in the
//      tenant's number and date format.
//   ✅ "Filter by additional details" — the shared _CustomFieldFilterPanel
//      the Contacts list uses. Its inputs join the board's own filter form
//      (#pbFilter) through form="pbFilter", so Apply sends the search, the
//      stage, the owner and the custom filters in one GET, and the board
//      and the table both honour them.
//   ✅ The search box also finds custom text values and dropdown choice
//      names (done by the API, through the shared CustomFieldListQuery).
//   ✅ A failed LOAD set [TempData] ErrorMessage and then RENDERED, so the
//      message showed on the next page instead of this one. It is
//      PageError now, an ordinary property the view shows. ErrorMessage
//      stays for OnPostUpdateStageAsync, whose JSON is followed by a
//      reload — the one case TempData is right for.
//
// CHANGES (022):
//   ✅ A follow-up the step could not create survives the board's reload
//      via TempData, instead of disappearing with the page.
//
// CHANGES (020 — Blueprint transitions):
//   ✅ The board reads the tenant's MATRIX, not a set of per-stage rules.
//      A drag onto a column the process has no step to is refused before
//      it posts, with the same wording the server would have used.
//   ✅ One note replaces the two reason fields — the transition carries
//      its own prompt, so the board asks the tenant's question rather than
//      "why was this lost?" every time.
//   ✅ Column headers show what a move INTO that stage needs, gathered
//      across the moves that lead there.
//
// CHANGES (019 — still true):
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

using MerkaiTrial.Admin.Web.Pages.Shared;              // 077
using MerkaiTrial.Admin.Web.Services.CustomFields;     // 077
using MerkaiTrial.Admin.Web.Services.Deals;
using MerkaiTrial.Admin.Web.Services.Pipeline;
using MerkaiTrial.Admin.Web.Services.Quotes;
using MerkaiTrial.Admin.Web.Services.Users;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Commands.PipelineStages;
using MerkaiTrial.Application.Configuration;           // 077
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
        private readonly ICustomFieldService _customFields;             // 077

        /// <summary>077. The board's filter form; the custom filter panel's inputs submit with it.</summary>
        public const string FilterFormId = "pbFilter";

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
            IPipelineRuleService ruleService,                           // ✅ 019
            ICustomFieldService customFields)                           // 077
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
            _customFields = customFields;
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
        /// The tenant's sales process — stages and the from-to matrix. Null
        /// only when the call failed; the board still works, it just posts
        /// without pre-checking and lets the server explain.
        /// </summary>
        public PipelineRulesDto? Rules { get; private set; }

        /// <summary>Live moves only, keyed "from|to" for the board's lookup.</summary>
        public Dictionary<string, TransitionDto> LiveMoves { get; private set; } = new();

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

        /// <summary>
        /// For the stage-move JSON handler only, whose response is followed
        /// by a reload — the next request, which is exactly where TempData
        /// shows. A failed page LOAD uses PageError instead.
        /// </summary>
        [TempData] public string? ErrorMessage { get; set; }
        [TempData] public string? SuccessMessage { get; set; }

        /// <summary>077. Shown on THIS response — a board that failed to load.</summary>
        public string? PageError { get; private set; }

        // ── 077: custom fields ─────────────────────────────────────────

        /// <summary>Active Deal custom fields shown as list columns, in order.</summary>
        public List<CustomFieldDefinitionDto> ListColumns { get; private set; } = new();

        /// <summary>The custom field filters: parsed, checked, ready for the API and the panel.</summary>
        public CustomFieldListFilterState CustomFilters { get; private set; } = new();

        private System.Globalization.CultureInfo? _culture;
        private string? _dateFormat;

        /// <summary>A list-column value as a person reads it. Empty when not filled in.</summary>
        public string ColumnValue(DealListItem deal, CustomFieldDefinitionDto field)
        {
            _culture    ??= CustomFieldFormatter.ResolveCulture(CultureName);
            _dateFormat ??= _tenantService.GetDateFormat();

            deal.CustomFieldValues.TryGetValue(field.Id, out var wire);

            // An unticked checkbox has no value; on a list, blank says "no"
            // more quietly than a column full of the word.
            if (field.FieldType == CustomFieldTypes.Checkbox)
                return wire == "true" ? "Yes" : string.Empty;

            return CustomFieldFormatter.Display(field, wire, _culture, _dateFormat);
        }

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

                // 077 — custom fields first: the filters go into the deals call.
                await LoadCustomFieldsAsync();

                _logger.LogInformation(
                    "Loading pipeline for TenantId={TenantId} Currency={Currency}",
                    tenantId, TenantCurrencyCode);

                var dealsTask = _dealService.GetAllAsync(tenantId, stage: StageFilter,
                                        search: SearchTerm, ownerUserId: OwnerFilter,
                                        pageSize: 500,
                                        customFilters: CustomFilters.Filters);   // 077
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

                LiveMoves = (Rules?.Transitions ?? new List<TransitionDto>())
                    .Where(t => t.IsActive)
                    .GroupBy(t => $"{t.FromStageKey}|{t.ToStageKey}")
                    .ToDictionary(g => g.Key, g => g.First());

                await LoadQuoteStatusForDealsAsync(tenantId);

                _logger.LogInformation("Loaded {Count} deals", Deals.Count);

                return Page();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load pipeline");
                PageError = "Failed to load pipeline. Please try again.";      // 077 — not TempData
                Deals = new();
                SalesTeam = new();
                DealQuoteStatus = new();
                return Page();
            }
        }

        // ── POST: Update Stage (Kanban drag & drop) ────────────────────

        /// <summary>
        /// 020: one note, whatever the transition asked for. The board asks
        /// for it before posting when its copy of the process says one is
        /// wanted, but the server is the authority — if the board's copy is
        /// stale, the refusal below explains exactly what is missing.
        /// </summary>
        public async Task<IActionResult> OnPostUpdateStageAsync(
            Guid dealId, string stage, string? note)
        {
            if (!await CanUpdateAsync())
                return new JsonResult(new { success = false, error = "You do not have permission to update deal stages." }) { StatusCode = 403 };

            try
            {
                var problems = await _dealStageService.MoveAsync(dealId, stage, note);

                // 022: the board reloads on success, so a problem has to
                // survive the round trip. TempData puts it in the global
                // alert the layout already renders.
                if (problems.Count > 0)
                    ErrorMessage = string.Join(" ", problems);

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

        // ── 020: the matrix, as the board needs it ────────────────────

        /// <summary>The configured move, or null when the process has none.</summary>
        public TransitionDto? MoveBetween(string? fromKey, string? toKey) =>
            fromKey is null || toKey is null
                ? null
                : LiveMoves.GetValueOrDefault($"{fromKey}|{toKey}");

        /// <summary>
        /// What a deal needs before any of the moves INTO this stage will
        /// take it. Gathered across every live move that leads here, so a
        /// column header can say "needs an accepted quote" without the rep
        /// having to open the settings page to find out.
        ///
        /// Union rather than intersection: if one route in wants an
        /// accepted quote and another does not, the header mentions it —
        /// over-warning is better than a rep discovering the rule only when
        /// the card bounces back.
        /// </summary>
        /// <summary>
        /// The live matrix as JSON for the board's drag handler, keyed
        /// "from|to". Serialised here rather than built in Razor so the
        /// labels and prompts — which are the tenant's own words and may
        /// contain quotes, apostrophes or Thai — are escaped properly
        /// rather than by hand in a string concatenation.
        /// </summary>
        public string ProcessJson =>
            System.Text.Json.JsonSerializer.Serialize(
                LiveMoves.ToDictionary(
                    kv => kv.Key,
                    kv => new
                    {
                        label = kv.Value.Label,
                        needsNote = kv.Value.RequiresNote,
                        prompt = kv.Value.NotePrompt
                    }),
                new System.Text.Json.JsonSerializerOptions
                {
                    // The values land inside a <script> block, so anything
                    // that could close it early is escaped.
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Default
                });

        public List<string> EntryHintsFor(string stageKey)
        {
            var into = LiveMoves.Values.Where(t => t.ToStageKey == stageKey).ToList();
            if (into.Count == 0) return new List<string>();

            var hints = new List<string>();
            if (into.Any(t => t.RequiresAcceptedQuote)) hints.Add("accepted quote");
            else if (into.Any(t => t.RequiresQuote))    hints.Add("a quote");
            if (into.Any(t => t.RequiresValue))         hints.Add("a value");
            if (into.Any(t => t.RequiresCloseDate))     hints.Add("a close date");
            if (into.Any(t => t.RequiresAttachment))    hints.Add("an attachment");
            if (into.Any(t => t.RequiresNote))          hints.Add("a note");

            return hints;
        }

        // ── 077: custom fields ─────────────────────────────────────────

        /// <summary>
        /// The Deal custom fields, the list columns, and the filters read
        /// from the query string. Non-fatal: the board works without them,
        /// and the panel says why they are missing.
        /// </summary>
        private async Task LoadCustomFieldsAsync()
        {
            List<CustomFieldDefinitionDto> fields;
            var failed = false;
            try
            {
                fields = await _customFields.GetDefinitionsAsync(CustomFieldEntityTypes.Deal, includeInactive: false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Custom fields could not be loaded for the pipeline");
                fields = new List<CustomFieldDefinitionDto>();
                failed = true;
            }

            ListColumns = fields
                .Where(f => f.IsActive && f.ShowInList)
                .OrderBy(f => f.SortOrder)
                .Take(CustomFieldLimits.MaxListColumns)
                .ToList();

            CustomFilters = CustomFieldListFilterState.Parse(Request.Query, fields);
            CustomFilters.FormId     = FilterFormId;
            CustomFilters.PageRoute  = "/Pipeline/Index";
            CustomFilters.LoadFailed = failed;
            CustomFilters.Noun       = "deal";
            CustomFilters.Culture    = CustomFieldFormatter.ResolveCulture(CultureName);
            CustomFilters.DateFormat = _tenantService.GetDateFormat();

            // "Clear these" keeps the board's own filters and the view.
            var clear = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(SearchTerm))  clear[nameof(SearchTerm)]  = SearchTerm.Trim();
            if (!string.IsNullOrWhiteSpace(StageFilter)) clear[nameof(StageFilter)] = StageFilter;
            if (!string.IsNullOrWhiteSpace(OwnerFilter)) clear[nameof(OwnerFilter)] = OwnerFilter;
            var view = Request.Query["View"].ToString();
            if (string.Equals(view, "table", StringComparison.OrdinalIgnoreCase)) clear["View"] = "table";
            CustomFilters.ClearRoute = clear;
        }

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
