// =====================================================================
// MODULES CONFIGURATION - Dynamic Module Definitions (Updated)
// Location: MerkaiTrial.Application/Configuration/ModulesConfiguration.cs
// =====================================================================

using MerkaiTrial.Application.Authorization;

namespace MerkaiTrial.Application.Configuration
{
    /// <summary>
    /// Central configuration for all system modules and their permissions
    /// </summary>
    public static class ModulesConfiguration
    {
        /// <summary>
        /// Modules a tenant can grant permissions over, for the role editor's
        /// checkbox grid. Projected from ModuleCatalog — there is no second
        /// list to drift.
        /// </summary>
        public static List<ModuleDefinition> GetAllModules() =>
            ModuleCatalog.All
                .OrderBy(m => m.SortOrder)
                .Select(m => new ModuleDefinition
                {
                    Name = m.Key,
                    DisplayName = m.DisplayName,
                    Icon = m.Icon,
                    Color = m.Group,
                    SortOrder = m.SortOrder,
                    Permissions = m.Actions.ToList(),
                })
                .ToList();

        public static string GetPermissionDisplayName(string permission) => permission switch
        {
            "create" => "Create",
            "read" => "Read",
            "update" => "Update",
            "delete" => "Delete",
            _ => permission
        };
    }

    public class ModuleDefinition
    {
        public string Name { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Icon { get; set; } = string.Empty;
        public string Color { get; set; } = string.Empty;
        public int SortOrder { get; set; }
        public List<string> Permissions { get; set; } = new();
    }
}
