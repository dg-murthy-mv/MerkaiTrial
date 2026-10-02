// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Settings/TaxRates/Index.cshtml.cs
//
// NEW FILE (058). The workspace's own tax rates.
//
// WHY THIS PAGE EXISTS
//   /Admin/TaxRates manages the SYSTEM rates — the standard VAT or GST
//   for a country, published once for every workspace that sells there.
//   Until now that was the only tax screen in the product, so a workspace
//   whose rate genuinely differs had nowhere to say so and no way to see
//   what rate its own quotes were using.
//
//   TaxRate.TenantId has always been the place for a workspace's own
//   rate, and GetDefaultTaxRateAsync has always been written to prefer it
//   over the system one. 056 made that work; this is the screen that uses
//   it.
//
// PERMISSIONS
//   ModuleName = "settings", the same module that gates pipeline stages
//   and the sales process. PermissionHandler bypasses it for a tenant
//   admin, so workspace admins can use this the day it ships and nobody
//   else can until it is granted under Roles & Permissions.
//
//   The API enforces the same thing again, and independently: 056's
//   TaxRateOwnership refuses a write to a system rate from a tenant
//   token, and refuses another workspace's rate with a 404. This page
//   HIDING the Edit button on a system row is a courtesy, not the
//   control.
//
// WHY SYSTEM RATES ARE LISTED HERE AT ALL
//   Because "which rate will my quotes use?" is the question this page
//   exists to answer, and the answer is often a system rate. Showing only
//   the workspace's own rows would leave the page empty for almost
//   everybody while their quotes were quietly being taxed at 18%.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.TaxRates;
using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Common;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.Admin.Web.Pages.Settings.TaxRates
{
    public class IndexModel : AuthorizedPageModel
    {
        private readonly ITaxRateService _taxRates;

        public IndexModel(
            IAuthorizationService authorizationService,
            ICurrentUserService currentUserService,
            ILogger<IndexModel> logger,
            ITaxRateService taxRates)
            : base(authorizationService, currentUserService, logger)
        {
            _taxRates = taxRates;
        }

        protected override string ModuleName => Modules.Settings;

        public List<TaxRateListItem> Rates { get; private set; } = new();

        /// <summary>The workspace's country — the only one its quotes use.</summary>
        public string CountryCode { get; private set; } = string.Empty;
        public string CountryName { get; private set; } = string.Empty;

        /// <summary>What this workspace calls tax: GST, VAT, …</summary>
        public string TaxLabel { get; private set; } = "Tax";

        /// <summary>
        /// The rate a new quote would actually use, as a percentage. Shown at
        /// the top, because it is the one number anybody opening this page
        /// wants — and until 056 nobody could see it anywhere.
        /// </summary>
        public decimal EffectiveRate { get; private set; }

        /// <summary>Where that rate came from, in words.</summary>
        public string EffectiveRateSource { get; private set; } = string.Empty;

        [TempData] public string? ErrorMessage { get; set; }

        public async Task<IActionResult> OnGetAsync()
        {
            var denied = await ValidatePermissionAsync(Actions.Read);
            if (denied is not null) return denied;

            await InitializePermissionsAsync();

            CountryCode = TenantCtx.GetCountryCode();
            CountryName = TenantCtx.GetCountryName();
            TaxLabel = TenantCtx.GetTaxLabel();

            await TryLoad("Tax rates", async () =>
            {
                // includeInactive: this is a management screen. A rate
                // switched off must stay visible or nothing can switch it
                // back on — the same one-way door 057 closed on the admin
                // list.
                var page = await _taxRates.GetAllAsync(
                    pageNumber: 1,
                    pageSize: 200,
                    countryFilter: CountryCode,
                    includeInactive: true);

                Rates = page?.Items ?? new List<TaxRateListItem>();
            });

            // The workspace's own rates first — they are the ones it can act
            // on, and the ones that win.
            Rates = Rates
                .OrderBy(r => r.IsSystem ? 1 : 0)
                .ThenByDescending(r => r.IsDefault)
                .ThenBy(r => r.Name)
                .ToList();

            ResolveEffectiveRate();

            return Page();
        }

        // ── the status helpers, computed once per request ────────────
        // AsOf is fixed here rather than read per row: several calls to
        // UtcNow in one render can disagree across midnight, which is
        // exactly when a scheduled rate change takes effect.
        private readonly DateTime _asOf = DateTime.UtcNow;

        public TaxRateState StateOf(TaxRateListItem r)
            => TaxRateStatus.Of(r.IsActive, r.EffectiveFrom, r.EffectiveTo, _asOf);

        public string StateLabel(TaxRateListItem r) => TaxRateStatus.Label(StateOf(r));
        public string StateBadge(TaxRateListItem r) => TaxRateStatus.BadgeClass(StateOf(r));
        public string StateExplain(TaxRateListItem r) => TaxRateStatus.Explain(StateOf(r));

        /// <summary>"1 Apr 2026 – 31 Mar 2027", "from 1 Apr 2026", "—".</summary>
        public string Window(TaxRateListItem r)
        {
            const string fmt = "d MMM yyyy";

            if (r.EffectiveFrom is null && r.EffectiveTo is null) return "—";
            if (r.EffectiveFrom is null) return $"until {r.EffectiveTo!.Value.ToString(fmt)}";
            if (r.EffectiveTo is null) return $"from {r.EffectiveFrom.Value.ToString(fmt)}";

            return $"{r.EffectiveFrom.Value.ToString(fmt)} – {r.EffectiveTo.Value.ToString(fmt)}";
        }

        /// <summary>A system rate is read-only here. 056 enforces it server-side.</summary>
        public bool CanEditRow(TaxRateListItem r) => CanUpdate && !r.IsSystem;

        /// <summary>
        /// Which rate a new quote resolves to, worked out from the same rows
        /// the list shows — the workspace's own default first, then the
        /// system default, and the country's figure if there is neither.
        ///
        /// This MIRRORS GetDefaultTaxRateAsync rather than calling it,
        /// because the point of showing it is to explain WHERE the number
        /// came from, which a bare decimal cannot do. If the two ever
        /// disagree, the rule in CurrentTenantService is the one that
        /// decides what a quote is taxed at — this is the explanation.
        /// </summary>
        private void ResolveEffectiveRate()
        {
            var inForce = Rates
                .Where(r => r.IsDefault && TaxRateStatus.IsInForce(
                    r.IsActive, r.EffectiveFrom, r.EffectiveTo, _asOf))
                .ToList();

            var mine = inForce.FirstOrDefault(r => !r.IsSystem);
            if (mine is not null)
            {
                EffectiveRate = mine.Rate;
                EffectiveRateSource = $"your own rate, “{mine.Name}”";
                return;
            }

            var system = inForce.FirstOrDefault(r => r.IsSystem);
            if (system is not null)
            {
                EffectiveRate = system.Rate;
                EffectiveRateSource = $"the standard rate for {CountryName}, “{system.Name}”";
                return;
            }

            EffectiveRate = 0m;
            EffectiveRateSource = "no rate is set — quotes fall back to the country default";
        }
    }
}
