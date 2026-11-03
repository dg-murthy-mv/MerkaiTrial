// =====================================================================
// ReportCatalog.cs
// Location: MerkaiTrial.Admin.Web/Pages/Reports/ReportCatalog.cs
//
// NEW (111). 112: Stalled Deals, Revenue Forecast, Lead Funnel & Conversion,
// Quote Performance and Invoice & Collections added. Every report, in the five categories the Reports page and
// the sidebar show them in. ONE list, so the page and the menu can never
// disagree, and a new report is one line here.
//
//   Sales Performance       — pipeline, people, activity, speed
//   Lead Analytics          — where leads come from and how they convert
//   Revenue & Finance       — invoiced, collected, owed
//   Customer Insights       — what is won and lost, and where
//   Operations & CRM Health — (no reports yet; the category appears when
//                              its first report is added)
//
// A category with no reports is not drawn. Advanced = needs the plan's
// "advanced_reports" feature (Professional and up), exactly as before.
//
// 113 — WHO may see them. Every report needs "reports.read". A category
// may name one more permission (Revenue & Finance → "financereports.read").
// The sidebar and the Reports page show only what the person may open
// (VisibleTo), and ReportBaseModel refuses the rest (ReportGate) — so
// typing the address is no way round it. Workspace admins see everything.
// =====================================================================

using System.Security.Claims;
using MerkaiTrial.Application.Authorization;

namespace MerkaiTrial.Admin.Web.Pages.Reports
{
    public sealed record ReportLink(
        string Key, string Page, string Title, string Description,
        string Icon, string IconBackground, string IconColour, bool Advanced = false);

    /// <summary>
    /// A report category. <paramref name="Permission"/> (113) is a module
    /// whose "read" is needed, on top of "reports", to see its reports.
    /// </summary>
    public sealed record ReportCategory(string Name, string Icon, IReadOnlyList<ReportLink> Reports,
        string? Permission = null);

    public static class ReportCatalog
    {
        public static readonly IReadOnlyList<ReportCategory> Categories = new List<ReportCategory>
        {
            new("Sales Performance", "bi-graph-up-arrow", new List<ReportLink>
            {
                new("pipeline", "/Reports/Pipeline/Index", "Sales Pipeline",
                    "Every open deal by stage today, with value and weighted value — and what was won and lost in the period.",
                    "bi-kanban-fill", "#eef2ff", "#4f46e5"),
                new("repperformance", "/Reports/RepPerformance/Index", "Sales Performance by Rep",
                    "Each person's open pipeline, deals won and lost, win rate and leads converted.",
                    "bi-person-check-fill", "#eff6ff", "#1d4ed8", Advanced: true),
                new("activityleaderboard", "/Reports/ActivityLeaderboard/Index", "Sales Activity",
                    "Notes, activities and tasks logged by each person.",
                    "bi-list-check", "#ecfeff", "#0e7490", Advanced: true),
                new("dealvelocity", "/Reports/DealVelocity/Index", "Deal Velocity",
                    "How long deals take: lead to deal, deal to close, and the time between stages.",
                    "bi-speedometer2", "#fefce8", "#a16207", Advanced: true),
                // 112
                new("stalled", "/Reports/StalledDeals/Index", "Stalled Deals",
                    "Open deals stuck in a stage, with nothing logged, or past their close date — and what they are worth.",
                    "bi-hourglass-split", "#fef2f2", "#b91c1c", Advanced: true),
                new("forecast", "/Reports/Forecast/Index", "Revenue Forecast",
                    "Open deals by the month they should close: value and weighted value (value × probability).",
                    "bi-graph-up-arrow", "#ecfdf5", "#047857", Advanced: true),
            }),
            new("Lead Analytics", "bi-funnel", new List<ReportLink>
            {
                new("funnel", "/Reports/SalesFunnel/Index", "Lead-to-Invoice Funnel",
                    "From lead to qualified, converted, quoted, accepted, invoiced and paid — with the rate at each step.",
                    "bi-funnel-fill", "#eff6ff", "#1d4ed8"),
                new("leadsources", "/Reports/LeadSources/Index", "Lead Sources",
                    "Which channels and sources bring the most leads, and which convert best.",
                    "bi-megaphone-fill", "#f0fdf4", "#15803d", Advanced: true),
                // 112
                new("leadfunnel", "/Reports/LeadFunnel/Index", "Lead Funnel & Conversion",
                    "Where the period's leads are now, how many qualified and converted, and how long converting took.",
                    "bi-funnel", "#eef2ff", "#4f46e5"),
            }),
            // 113 — needs Finance Reports as well as Reports.
            new("Revenue & Finance", "bi-cash-coin", Permission: Modules.FinanceReports, Reports: new List<ReportLink>
            {
                new("revenue", "/Reports/Revenue/Index", "Invoiced & Collected",
                    "Invoiced by month of issue; collected by the month the money arrived.",
                    "bi-cash-stack", "#ecfdf5", "#047857"),
                new("outstanding", "/Reports/Outstanding/Index", "Outstanding Invoices",
                    "Everything still owed today, by how overdue it is: current, 1–30, 31–60, 61–90, 90+ days.",
                    "bi-receipt-cutoff", "#fff7ed", "#c2410c"),
                // 112
                new("quoteperformance", "/Reports/QuotePerformance/Index", "Quote Performance",
                    "Quotes sent, accepted, rejected and expiring; acceptance rate and average discount.",
                    "bi-file-earmark-text", "#eff6ff", "#1d4ed8"),
                new("collections", "/Reports/InvoiceCollections/Index", "Invoice & Collections",
                    "How fast invoices are paid, how much arrives, payment methods, and who is overdue.",
                    "bi-wallet2", "#f0fdf4", "#15803d"),
            }),
            new("Customer Insights", "bi-people", new List<ReportLink>
            {
                new("winloss", "/Reports/WinLoss/Index", "Win / Loss",
                    "Deals won and lost in the period, the win rate and won value — by owner.",
                    "bi-trophy-fill", "#fef9c3", "#a16207"),
                new("vertical", "/Reports/ByVertical/Index", "Revenue by Vertical",
                    "Which industries bring the most pipeline, won revenue and invoiced value.",
                    "bi-grid-fill", "#f5f3ff", "#6d28d9"),
            }),
            new("Operations & CRM Health", "bi-heart-pulse", new List<ReportLink>()),
        };

        // ── 113: who may see what ─────────────────────────────────────

        /// <summary>
        /// The report a page belongs to, by its page path
        /// ("/Reports/StalledDeals/Index"). Null for a page not in the list.
        /// </summary>
        public static (ReportCategory Category, ReportLink Link)? Find(string? page)
        {
            if (string.IsNullOrWhiteSpace(page)) return null;
            foreach (var c in Categories)
                foreach (var r in c.Reports)
                    if (string.Equals(r.Page, page, StringComparison.OrdinalIgnoreCase))
                        return (c, r);
            return null;
        }

        /// <summary>
        /// The categories this person may see, with at least one report.
        /// For DRAWING the menu and the Reports page; the gate on the pages
        /// themselves is ReportGate, through the real permission handler.
        /// </summary>
        public static IReadOnlyList<ReportCategory> VisibleTo(ClaimsPrincipal user) =>
            Categories
                .Where(c => c.Reports.Count > 0)
                .Where(c => c.Permission is null || Can(user, c.Permission))
                .ToList();

        /// <summary>
        /// "module.read" held, or a workspace admin. Compared the way
        /// PermissionHandler and the sidebar's CanNav compare: ignoring case,
        /// because role JSON says "Reports" and the policies say "reports".
        /// </summary>
        public static bool Can(ClaimsPrincipal user, string module, string action = "read")
        {
            if (user.HasClaim(c => c.Type == "IsTenantAdmin" &&
                                   string.Equals(c.Value, "true", StringComparison.OrdinalIgnoreCase)))
                return true;

            var needed = $"{module}.{action}";
            return user.Claims.Any(c =>
                string.Equals(c.Type, "permission", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(c.Value, needed, StringComparison.OrdinalIgnoreCase));
        }
    }
}
