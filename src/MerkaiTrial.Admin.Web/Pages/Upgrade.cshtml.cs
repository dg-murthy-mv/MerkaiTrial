// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Upgrade.cshtml.cs
//
// NEW (114). Where a page that is not in the workspace's plan sends people
// (Filters/PlanFeaturePageFilter, and ReportGate for Advanced reports
// reached another way). Not an error: it says what the feature does, which
// plans include it, and who to ask — and the rest of the CRM works as
// before.
//
// The plans listed are the PUBLIC, active plans whose Features include the
// key, cheapest first, read live from the Plans table — so the page agrees
// with whatever the super admin sets up in Admin → Plans.
// =====================================================================

using MerkaiTrial.Application.Configuration;
using MerkaiTrial.Application.Services.Tenants;
using MerkaiTrial.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace MerkaiTrial.Admin.Web.Pages;

public class UpgradeModel : PageModel
{
    private readonly ICurrentTenantService _tenant;
    private readonly FlowDbContext _db;

    public UpgradeModel(ICurrentTenantService tenant, FlowDbContext db)
    {
        _tenant = tenant;
        _db = db;
    }

    [BindProperty(SupportsGet = true)] public string? Feature { get; set; }

    public string FeatureKey { get; private set; } = string.Empty;
    public string FeatureLabel { get; private set; } = "This feature";
    public string? WhatItDoes { get; private set; }
    public string TenantName { get; private set; } = string.Empty;
    public string PlanName { get; private set; } = string.Empty;
    public List<string> IncludedIn { get; private set; } = new();

    /// <summary>One plain sentence per enforced feature (PlanFeatureKeys).</summary>
    private static readonly Dictionary<string, string> Descriptions = new(StringComparer.OrdinalIgnoreCase)
    {
        [PlanFeatureKeys.AdvancedReports] =
            "Sales Performance by Rep, Sales Activity, Deal Velocity, Stalled Deals, Revenue Forecast and Lead Sources.",
        [PlanFeatureKeys.BasicReports] =
            "The Reports section: pipeline, funnel, revenue, outstanding invoices and win/loss.",
        [PlanFeatureKeys.EmailIntegration] =
            "A receiving address for your workspace: customers' replies are filed on the right lead, deal or contact, and anything else waits in the shared Inbox.",
        [PlanFeatureKeys.CustomFields] =
            "Your own extra fields on contacts and companies.",
        [PlanFeatureKeys.CustomPipeline] =
            "Changing your pipeline stages, your lead statuses and the sales process (which moves are allowed and what each one needs).",
        [PlanFeatureKeys.CustomRoles] =
            "Creating new roles. You can already change what the roles you have may do.",
        [PlanFeatureKeys.AuditLogs] =
            "The Activity Log: who changed what in your workspace, and when.",
    };

    public async Task OnGetAsync()
    {
        FeatureKey = (Feature ?? string.Empty).Trim();
        if (FeatureKey.Length > 0)
        {
            FeatureLabel = FeatureCatalog.Resolve(FeatureKey).Label;
            WhatItDoes = Descriptions.TryGetValue(FeatureKey, out var d) ? d : null;
        }

        try { TenantName = _tenant.GetTenantName(); } catch { TenantName = "your workspace"; }
        try { PlanName = _tenant.GetPlanDisplayName(); } catch { PlanName = "current"; }

        if (FeatureKey.Length > 0)
        {
            // Features is a JSON array of keys: match the quoted key exactly.
            var quoted = "\"" + FeatureKey + "\"";
            IncludedIn = await _db.Plans.AsNoTracking()
                .Where(p => p.IsActive && p.IsPublic && p.Features != null && p.Features.Contains(quoted))
                .OrderBy(p => p.SortOrder)
                .Select(p => p.DisplayName)
                .ToListAsync();
        }
    }
}
