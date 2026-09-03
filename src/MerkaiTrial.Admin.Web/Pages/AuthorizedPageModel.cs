// FILE: MerkaiTrial.Admin.Web/Pages/AuthorizedPageModel.cs
// REPLACE YOUR EXISTING AuthorizedPageModel.cs
//
// ✅ MERGED: plan/quota/trial helpers folded in from AppPageModel, so pages
// migrating off AppPageModel onto this base class (Companies, Contacts)
// don't lose that functionality. CanUser/CanCreate(module)/CanRead(module)/
// etc from AppPageModel were intentionally NOT carried over — they'd
// collide with this class's own CanCreate/CanRead/CanUpdate/CanDelete
// properties below, and those AppPageModel methods read permission claims
// directly with a case-sensitive comparison (the same bug PermissionHandler
// had before it was fixed) rather than going through PermissionHandler at
// all. The ValidatePermissionAsync/CanCreateAsync/etc below are the
// case-fixed, single-source-of-truth path — use those instead.

using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Services;
using MerkaiTrial.Application.Services.Tenants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.DependencyInjection;
using System.Linq;

namespace MerkaiTrial.Admin.Web.Pages
{
    public abstract class AuthorizedPageModel : PageModel
    {
        protected readonly IAuthorizationService AuthorizationService;
        protected readonly ICurrentUserService CurrentUserService;
        protected readonly ILogger Logger;

        protected AuthorizedPageModel(
            IAuthorizationService authorizationService,
            ICurrentUserService currentUserService,
            ILogger logger)
        {
            AuthorizationService = authorizationService;
            CurrentUserService = currentUserService;
            Logger = logger;
        }

        // ── ✅ Lazy-resolved ICurrentTenantService — same pattern AppPageModel
        // used. Lazy via HttpContext.RequestServices rather than constructor
        // injection so no existing AuthorizedPageModel subclass (Products,
        // and now Companies/Contacts) needs its constructor signature touched.
        private ICurrentTenantService? _tenantCtx;
        protected ICurrentTenantService TenantCtx =>
            _tenantCtx ??= HttpContext.RequestServices
                .GetRequiredService<ICurrentTenantService>();

        // ── MODULE NAME ──────────────────────────────────────────────────
        // The module this page belongs to. Drives every policy name built by
        // ValidatePermissionAsync / CanCreateAsync / etc.
        //
        // ✅ CROSS-MODULE PAGES: a page that aggregates several modules and owns
        // none of them (Dashboard) returns string.Empty and uses the UserCan*
        // helpers instead. The guard below makes that explicit: calling a
        // module-scoped method on such a page throws instead of quietly building
        // a nonsense policy name like ".read" and denying everything.
        protected abstract string ModuleName { get; }

        private void RequireModuleName(string caller)
        {
            if (string.IsNullOrWhiteSpace(ModuleName))
                throw new InvalidOperationException(
                    $"{GetType().Name} has no ModuleName, so {caller} cannot build a policy name. " +
                    "This page spans multiple modules — use UserCan/UserCanRead/UserCanCreate/" +
                    "UserCanUpdate/UserCanDelete(module) instead.");
        }

        // Permission check methods using policies
        protected async Task<bool> CanCreateAsync()
        {
            RequireModuleName(nameof(CanCreateAsync));
            var policy = GetPolicy(Actions.Create);
            var result = await AuthorizationService.AuthorizeAsync(User, policy);
            return result.Succeeded;
        }

        protected async Task<bool> CanReadAsync()
        {
            RequireModuleName(nameof(CanReadAsync));
            var policy = GetPolicy(Actions.Read);
            var result = await AuthorizationService.AuthorizeAsync(User, policy);
            return result.Succeeded;
        }

        protected async Task<bool> CanUpdateAsync()
        {
            RequireModuleName(nameof(CanUpdateAsync));
            var policy = GetPolicy(Actions.Update);
            var result = await AuthorizationService.AuthorizeAsync(User, policy);
            return result.Succeeded;
        }

        protected async Task<bool> CanDeleteAsync()
        {
            RequireModuleName(nameof(CanDeleteAsync));
            var policy = GetPolicy(Actions.Delete);
            var result = await AuthorizationService.AuthorizeAsync(User, policy);
            return result.Succeeded;
        }

        // Validate permission (redirects to access denied if no access)
        protected async Task<IActionResult?> ValidatePermissionAsync(string action)
        {
            RequireModuleName(nameof(ValidatePermissionAsync));

            // TenantAdmin has full access to everything — bypass individual permission checks.
            //
            // ✅ AUDIT FIX: this short-circuit used to return before any logging, and it
            // never calls AuthorizationService, so PermissionHandler never ran either.
            // Result: for a tenant_admin the page-entry gate produced ZERO log lines,
            // making "gate ran and was bypassed" indistinguishable from "no gate exists"
            // in the Serilog output. The two lines below mirror PermissionHandler's own
            // wording so both paths read identically in the log.
            var isTenantAdmin = User.FindFirst("IsTenantAdmin")?.Value;
            if (string.Equals(isTenantAdmin, "true", StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogInformation("🔐 Checking permission: {Module}.{Action} for user {User}",
                    ModuleName, action, DescribeUser());
                Logger.LogInformation("✅ Access granted (TenantAdmin bypass) for {User}", DescribeUser());
                return null;
            }

            var result = await AuthorizationService.AuthorizeAsync(User, null, $"{ModuleName}.{action}");
            if (!result.Succeeded)
            {
                Logger.LogWarning("❌ Access denied (missing permission: {Module}.{Action}) for user {User} (ID: {UserId})",
                    ModuleName, action,
                    DescribeUser(),
                    User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value);
                return RedirectToPage("/AccessDenied");
            }
            return null;
        }

        // Get policy name based on module and action
        private string GetPolicy(string action)
        {
            return $"{ModuleName}.{action}";
        }

        // ── ✅ AUDIT HELPER ──────────────────────────────────────────────────
        // Best-effort user description for permission log lines. Falls back
        // through email → name → NameIdentifier → "Unknown" so the audit trail
        // records SOMETHING identifying rather than a bare "Unknown" on every
        // line. NOTE: PermissionHandler has its own copy of this problem — the
        // "for user Unknown" seen throughout the current logs originates there,
        // not here. Fix that separately with the same fallback chain.
        protected string DescribeUser()
        {
            var email = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;
            if (!string.IsNullOrWhiteSpace(email)) return email;

            var name = User.Identity?.Name;
            if (!string.IsNullOrWhiteSpace(name)) return name;

            var id = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            return string.IsNullOrWhiteSpace(id) ? "Unknown" : id;
        }

        // Properties for UI (exposed to Razor pages)
        public bool CanCreate { get; set; }
        public bool CanRead { get; set; }
        public bool CanUpdate { get; set; }
        public bool CanDelete { get; set; }

        // Initialize permissions in OnGet
        protected async Task InitializePermissionsAsync()
        {
            CanCreate = await CanCreateAsync();
            CanRead = await CanReadAsync();
            CanUpdate = await CanUpdateAsync();
            CanDelete = await CanDeleteAsync();
        }

        // ── ✅ CROSS-MODULE PERMISSION HELPERS ──────────────────────────────
        // CanCreate/CanRead/CanUpdate/CanDelete above are properties bound to
        // THIS page's own ModuleName. Some views need to check a DIFFERENT
        // module — e.g. the Companies Detail page's "Add Contact" button,
        // gated by Contacts.create while the page itself is Companies. A
        // property can't take a parameter, and a method can't share a name
        // with a property in the same class, so these are separate,
        // distinctly-named methods. Reads the permission claim directly
        // (case-insensitive, same fix as PermissionHandler) since the claim
        // is already on User — no need to round-trip through
        // AuthorizationService for a check this cheap.
        public bool UserCan(string module, string action)
        {
            var isTenantAdmin = User.FindFirst("IsTenantAdmin")?.Value;
            if (string.Equals(isTenantAdmin, "true", StringComparison.OrdinalIgnoreCase))
                return true;

            var permClaim = $"{module}.{action}";
            return User.Claims.Any(c =>
                c.Type == "permission" &&
                string.Equals(c.Value, permClaim, StringComparison.OrdinalIgnoreCase));
        }

        public bool UserCanCreate(string module) => UserCan(module, "create");
        public bool UserCanRead(string module) => UserCan(module, "read");
        public bool UserCanUpdate(string module) => UserCan(module, "update");
        public bool UserCanDelete(string module) => UserCan(module, "delete");

        // ── ✅ TENANT-AWARE FORMATTING ─────────────────────────────────────
        // Centralised here so every page inheriting this base can format money
        // and dates correctly without its own passthrough. Previously each page
        // model declared its own, which is why several views fell back to raw
        // .ToString("N2") / .ToString("MMM dd, yyyy") — the helper simply wasn't
        // available on that page, so whoever wrote the view reached for the
        // framework method instead. Those render with the SERVER's culture and
        // UTC clock, producing Indian lakh grouping on Thai tenants and dates
        // off by the tenant's UTC offset.

        /// <summary>
        /// Tenant-aware money. Grouping follows Countries.NumberFormat for the
        /// tenant's country (lakh for en-IN, Western for th-TH), NOT the server
        /// culture. Returns symbol + amount, e.g. "฿255,000.00".
        /// Pass decimals to override Country.CurrencyDecimals (0 for KPI tiles).
        /// NEVER format money with .ToString("N2") in a view — use this.
        /// </summary>
        public string FormatCurrency(decimal amount, int? decimals = null)
            => TenantCtx.FormatCurrency(amount, decimals);

        /// <summary>Bare currency symbol from Countries, e.g. "฿". Falls back to ISO code.</summary>
        public string CurrencySymbol => TenantCtx.GetCurrencySymbol();

        /// <summary>ISO currency code for this tenant, e.g. "THB".</summary>
        public string CurrencyCode => TenantCtx.GetCurrencyCode();

        // ── ✅ CLIENT-SIDE FORMATTING SUPPORT ──────────────────────────────
        // Server-rendered money goes through FormatCurrency above. But the
        // Create/Edit pages recalculate line totals in JavaScript as the user
        // types, and those used `${symbol} ${x.toFixed(2)}` — toFixed NEVER
        // groups, so a 6-figure total rendered "255000.00" client-side while
        // the same value rendered "฿255,000.00" once saved and re-read.
        //
        // These three feed data-* attributes on <body> in _Layout.cshtml so a
        // shared Intl.NumberFormat can mirror the server exactly. Intl and .NET
        // both use CLDR, so "en-IN" gives lakh grouping in both.
        //
        // NOTE: CultureName is the raw Countries.NumberFormat value. Legacy rows
        // may still hold a .NET pattern like "#,##0.00" rather than a culture
        // name; the JS helper falls back to the browser default in that case,
        // exactly as FormatCurrency falls back to InvariantCulture.

        /// <summary>Culture name for this tenant, e.g. "en-IN" / "th-TH". For Intl.NumberFormat.</summary>
        public string CultureName => TenantCtx.GetNumberFormat();

        /// <summary>Decimal places for this tenant's currency (Country.CurrencyDecimals).</summary>
        public int CurrencyDecimals => TenantCtx.GetCurrencyDecimals();

        /// <summary>
        /// UTC → tenant-local date, using the tenant country's timezone and
        /// date format. NEVER render a *Utc property directly in a view.
        /// </summary>
        public string FormatDate(DateTime utcDateTime)
            => TenantCtx.FormatDate(utcDateTime);

        /// <summary>UTC → tenant-local date and time.</summary>
        public string FormatDateTime(DateTime utcDateTime)
            => TenantCtx.FormatDateTime(utcDateTime);

        // ── ✅ PLAN & FEATURE HELPERS (merged from AppPageModel) ───────────

        /// <summary>
        /// Check if a feature is enabled for this tenant's plan.
        /// Use in cshtml: @if (HasFeature("lead_scoring"))
        /// </summary>
        public bool HasFeature(string featureKey)
            => TenantCtx.HasFeature(featureKey);

        /// <summary>Plan name for display: "Starter" / "Professional" / "Enterprise"</summary>
        public string PlanDisplayName
            => TenantCtx.GetPlanDisplayName();

        /// <summary>Raw plan name: "starter" / "professional" / "enterprise"</summary>
        public string PlanName
            => TenantCtx.GetPlanName();

        /// <summary>Badge CSS class for the current plan</summary>
        public string PlanBadgeClass => PlanName switch
        {
            "starter" => "bg-secondary",
            "professional" => "bg-primary",
            "enterprise" => "bg-warning text-dark",
            _ => "bg-secondary"
        };

        /// <summary>Plan upgrade prompt text — shown next to locked features</summary>
        public string UpgradePrompt => PlanName switch
        {
            "starter" => "Upgrade to Professional",
            "professional" => "Upgrade to Enterprise",
            _ => string.Empty
        };

        // ── ✅ QUOTA HELPERS (merged from AppPageModel) ────────────────────

        public int MaxLeads => TenantCtx.GetMaxLeads();
        public int MaxDeals => TenantCtx.GetMaxDeals();
        public int MaxUsers => TenantCtx.GetMaxUsers();
        public int MaxContacts => TenantCtx.GetMaxContacts();
        public int MaxCompanies => TenantCtx.GetMaxCompanies();

        /// <summary>
        /// Quota progress percent (0–100). Pass current count from page model.
        /// Use: @Model.QuotaPercent(Model.Stats.TotalLeads, Model.MaxLeads)
        /// </summary>
        public static int QuotaPercent(int current, int max)
            => max <= 0 ? 0 : Math.Min(100, (int)Math.Round((double)current / max * 100));

        /// <summary>Bootstrap color class based on quota usage</summary>
        public static string QuotaBarClass(int current, int max)
        {
            var pct = QuotaPercent(current, max);
            return pct >= 90 ? "bg-danger" : pct >= 70 ? "bg-warning" : "bg-success";
        }

        /// <summary>True when at or over the plan limit</summary>
        public static bool IsAtLimit(int current, int max)
            => current >= max;

        // ── ✅ TRIAL HELPERS (merged from AppPageModel) ────────────────────

        public bool IsOnTrial => TenantCtx.IsOnTrial();
        public bool IsTrialExpired => TenantCtx.IsTrialExpired();
        public int TrialDaysRemaining => TenantCtx.GetTrialDaysRemaining();
        public bool IsAccountActive => TenantCtx.IsAccountActive();

        // Trial banner — show on every page when trial is active
        public string? TrialBannerMessage
        {
            get
            {
                if (IsTrialExpired)
                    return "Your trial has expired. Upgrade to continue using Merkai.";
                if (IsOnTrial)
                {
                    var days = TrialDaysRemaining;
                    return days <= 7
                        ? $"⚠️ Your trial expires in {days} day{(days == 1 ? "" : "s")}. Upgrade now to avoid losing access."
                        : $"You have {days} days remaining in your free trial.";
                }
                return null;
            }
        }

        protected async Task TryLoad(string context, Func<Task> action)
        {
            try { await action(); }
            catch (HttpRequestException ex)
            {
                Logger.LogWarning(ex, "{Context}: API call failed", context);
                TempData["Error"] = $"{context}: {ex.Message}";
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "{Context}: unexpected error", context);
                TempData["Error"] = $"{context}: unexpected error";
            }
        }
    }
}
