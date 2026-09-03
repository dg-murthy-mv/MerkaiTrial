using System.Collections.Generic;

namespace MerkaiTrial.Admin.Web.Services
{
    public class NavigationService
    {
        public List<NavSection> GetNavSections()
        {
            return new List<NavSection>
            {
                // 🟢 MAIN Section - Green Theme
                new NavSection
                {
                    Id = "main",
                    Title = "Main",
                    Icon = "bi-grid-fill",
                    Emoji = "🟢",
                    GradientFrom = "#10b981",
                    GradientTo = "#059669",
                    BorderColor = "#10b981",
                    GlowColor = "rgba(16, 185, 129, 0.4)",
                    Items = new List<NavItem>
                    {
                        new NavItem { Icon = "bi-speedometer2", Emoji = "🟢", Text = "Dashboard", Page = "/Dashboard/Index", IsPrefix = true },
                        new NavItem { Icon = "bi-people", Emoji = "🟢", Text = "Contacts", Page = "/Contacts" },
                        new NavItem { Icon = "bi-bullseye", Emoji = "🟢", Text = "Leads", Page = "/Leads" }
                    }
                },
                
                // 🔵 SALES Section - Blue Theme
                new NavSection
                {
                    Id = "sales",
                    Title = "Sales",
                    Icon = "bi-cart-fill",
                    Emoji = "🔵",
                    GradientFrom = "#3b82f6",
                    GradientTo = "#2563eb",
                    BorderColor = "#3b82f6",
                    GlowColor = "rgba(59, 130, 246, 0.4)",
                    Items = new List<NavItem>
                    {
                        new NavItem { Icon = "bi-file-earmark-text", Emoji = "🔵", Text = "Quotes", Page = "/Quotes/Index", IsPrefix = true },
                        new NavItem { Icon = "bi-receipt", Emoji = "🔵", Text = "Invoices", Page = "/Invoices/Index", IsPrefix = true },
                        new NavItem { Icon = "bi-box-seam", Emoji = "🔵", Text = "Products", Page = "/Products/Index", IsPrefix = true }
                    }
                },
                
                // 🟣 REPORTS Section - Purple Theme
                new NavSection
                {
                    Id = "reports",
                    Title = "Reports",
                    Icon = "bi-bar-chart-fill",
                    Emoji = "🟣",
                    GradientFrom = "#8b5cf6",
                    GradientTo = "#7c3aed",
                    BorderColor = "#8b5cf6",
                    GlowColor = "rgba(139, 92, 246, 0.4)",
                    Items = new List<NavItem>
                    {
                        new NavItem { Icon = "bi-graph-up", Emoji = "🟣", Text = "Overview", Page = "/Reports/Index" },
                        new NavItem { Icon = "bi-bag-check", Emoji = "🟣", Text = "Sales by Product", Page = "/Reports/SalesByProduct" },
                        new NavItem { Icon = "bi-cash-coin", Emoji = "🟣", Text = "Collections", Page = "/Reports/Collections" }
                    }
                },
                
                // 🟠 MANAGEMENT Section - Orange Theme (for Tenants, Users, Roles)
               // 🟠 MANAGEMENT Section
                new NavSection
                {
                    Id = "management",
                    Title = "Management",
                    Icon = "bi-gear-fill",
                    Emoji = "🟠",
                    GradientFrom = "#f59e0b",
                    GradientTo = "#d97706",
                    BorderColor = "#f59e0b",
                    GlowColor = "rgba(245, 158, 11, 0.4)",
                    Items = new List<NavItem>
                    {
                        new NavItem { Icon = "bi-building", Emoji = "🟠", Text = "Tenants", Page = "/Tenants/Index", IsPrefix = true },
                        new NavItem { Icon = "bi-person-badge", Emoji = "🟠", Text = "Users", Page = "/Users/Index", IsPrefix = true },
                        new NavItem { Icon = "bi-shield-lock", Emoji = "🟠", Text = "Roles", Page = "/Roles/Index", IsPrefix = true }
                    }
                }
                
                // 🌸 FUTURE: Automation Section (Commented for Phase 3)
                /*
                new NavSection
                {
                    Id = "automation",
                    Title = "Automation",
                    Icon = "bi-robot",
                    Emoji = "🌸",
                    GradientFrom = "#ec4899",
                    GradientTo = "#db2777",
                    BorderColor = "#ec4899",
                    GlowColor = "rgba(236, 72, 153, 0.4)",
                    Items = new List<NavItem>
                    {
                        new NavItem { Icon = "bi-calendar-check", Emoji = "🌸", Text = "Follow-ups", Page = "/FollowUps" },
                        new NavItem { Icon = "bi-kanban", Emoji = "🌸", Text = "Pipelines", Page = "/Pipelines" }
                    }
                }
                */
            };
        }
    }

    public class NavSection
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Icon { get; set; } = string.Empty;
        public string Emoji { get; set; } = string.Empty;
        public string GradientFrom { get; set; } = string.Empty;
        public string GradientTo { get; set; } = string.Empty;
        public string BorderColor { get; set; } = string.Empty;
        public string GlowColor { get; set; } = string.Empty;
        public List<NavItem> Items { get; set; } = new List<NavItem>();
    }

    public class NavItem
    {
        public string Icon { get; set; } = string.Empty;
        public string Emoji { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public string Page { get; set; } = string.Empty;
        public bool IsPrefix { get; set; }
        public string? Badge { get; set; }
    }
}
