// =====================================================================
// MODULES CONFIGURATION - Dynamic Module Definitions (Updated)
// Location: MerkaiTrial.Application/Configuration/ModulesConfiguration.cs
// =====================================================================

namespace MerkaiTrial.Application.Configuration
{
    /// <summary>
    /// Central configuration for all system modules and their permissions
    /// </summary>
    public static class ModulesConfiguration
    {
        /// <summary>
        /// Get all available modules with their permissions (sorted for sidebar)
        /// </summary>
        public static List<ModuleDefinition> GetAllModules()
        {
            return new List<ModuleDefinition>
            {
                // -------- Contacts & Accounts --------
                new ModuleDefinition
                {
                    Name = "company-verticals",
                    DisplayName = "Company Verticals",
                    Icon = "bi-diagram-3",
                    Color = "teal",
                    SortOrder = 10,
                    Permissions = new List<string> { "create", "read", "update", "delete" }
                },
                new ModuleDefinition
                {
                    Name = "tax-rates",
                    DisplayName = "Tax Rates",
                    Icon = "bi-percent",
                    Color = "teal",
                    SortOrder = 11,
                    Permissions = new List<string> { "create", "read", "update", "delete" }
                },
                new ModuleDefinition
                {
                    Name = "companies",
                    DisplayName = "Companies",
                    Icon = "bi-building",
                    Color = "teal",
                    SortOrder = 12,
                    Permissions = new List<string> { "create", "read", "update", "delete" }
                },
                new ModuleDefinition
                {
                    Name = "contacts",
                    DisplayName = "Contacts",
                    Icon = "bi-people",
                    Color = "teal",
                    SortOrder = 13,
                    Permissions = new List<string> { "create", "read", "update", "delete" }
                },
                new ModuleDefinition
                {
                    Name = "products",
                    DisplayName = "Products",
                    Icon = "bi-funnel",
                    Color = "teal",
                    SortOrder = 61,
                    Permissions = new List<string> { "create", "read", "update", "delete" }
                },
                new ModuleDefinition
                {
                    Name = "leads",
                    DisplayName = "Leads",
                    Icon = "bi-funnel",
                    Color = "teal",
                    SortOrder = 14,
                    Permissions = new List<string> { "create", "read", "update", "delete" }
                },

                // -------- Sales Pipeline --------
                // Keep module key "deals" if your backend uses it, but show "Pipeline" in the UI
                new ModuleDefinition
                {
                    Name = "deals",
                    DisplayName = "Pipeline",
                    Icon = "bi-kanban",
                    Color = "purple",
                    SortOrder = 20,
                    Permissions = new List<string> { "create", "read", "update", "delete" }
                },
                new ModuleDefinition
                {
                    Name = "quotes",
                    DisplayName = "Quotes",
                    Icon = "bi-file-earmark-text",
                    Color = "purple",
                    SortOrder = 21,
                    Permissions = new List<string> { "create", "read", "update", "delete" }
                },
                new ModuleDefinition
                {
                    Name = "invoices",
                    DisplayName = "Invoices",
                    Icon = "bi-receipt",
                    Color = "purple",
                    SortOrder = 22,
                    Permissions = new List<string> { "create", "read", "update", "delete" }
                },
                new ModuleDefinition
                {
                    Name = "payments",
                    DisplayName = "Payments",
                    Icon = "bi-cash-coin",
                    Color = "purple",
                    SortOrder = 23,
                    Permissions = new List<string> { "create", "read", "update", "delete" }
                },

                // -------- Catalog / Master Data (optional, if you use Products) --------
                new ModuleDefinition
                {
                    Name = "products",
                    DisplayName = "Products",
                    Icon = "bi-box-seam",
                    Color = "indigo",
                    SortOrder = 30,
                    Permissions = new List<string> { "create", "read", "update", "delete" }
                },

                // -------- Admin / System --------
                new ModuleDefinition
                {
                    Name = "countries",
                    DisplayName = "Countries",
                    Icon = "bi-globe2",
                    Color = "slate",
                    SortOrder = 40,
                    Permissions = new List<string> { "create", "read", "update", "delete" }
                },
                new ModuleDefinition
                {
                    Name = "users",
                    DisplayName = "Users",
                    Icon = "bi-person-badge",
                    Color = "slate",
                    SortOrder = 41,
                    Permissions = new List<string> { "create", "read", "update", "delete" }
                },
                new ModuleDefinition
                {
                    Name = "tenants",
                    DisplayName = "Tenants",
                    Icon = "bi-buildings",
                    Color = "slate",
                    SortOrder = 42,
                    Permissions = new List<string> { "create", "read", "update", "delete" }
                },
                new ModuleDefinition
                {
                    Name = "reports",
                    DisplayName = "Reports",
                    Icon = "bi-bar-chart",
                    Color = "slate",
                    SortOrder = 50,
                    Permissions = new List<string> { "view", "create", "delete" }
                },
                new ModuleDefinition
                {
                    Name = "settings",
                    DisplayName = "Settings",
                    Icon = "bi-gear",
                    Color = "slate",
                    SortOrder = 60,
                    Permissions = new List<string> { "view", "update" }
                },
            };
        }

        /// <summary>
        /// Get permission display name
        /// </summary>
        public static string GetPermissionDisplayName(string permission)
        {
            return permission switch
            {
                "create" => "Create",
                "read" => "Read",
                "update" => "Update",
                "delete" => "Delete",
                "view" => "View",
                _ => permission
            };
        }
    }

    /// <summary>
    /// Module definition with permissions
    /// </summary>
    public class ModuleDefinition
    {
        public string Name { get; set; } = string.Empty;        // stable key used in routes/permissions
        public string DisplayName { get; set; } = string.Empty; // label in UI
        public string Icon { get; set; } = string.Empty;        // e.g., Bootstrap Icon class
        public string Color { get; set; } = string.Empty;       // optional theme tag
        public int SortOrder { get; set; }                      // ordering in sidebar
        public List<string> Permissions { get; set; } = new();  // create/read/update/delete/view
    }
}
