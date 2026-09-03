using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MerkaiTrial.Application.Configuration
{
    public static class ProductCategoriesConfiguration
    {
        public static readonly List<ProductCategory> Categories = new()
        {
            new ProductCategory { Name = "Electronics", Icon = "bi-lightning", Color = "#3b82f6" },
            new ProductCategory { Name = "Software", Icon = "bi-code-square", Color = "#8b5cf6" },
            new ProductCategory { Name = "Services", Icon = "bi-tools", Color = "#10b981" },
            new ProductCategory { Name = "Hardware", Icon = "bi-cpu", Color = "#6366f1" },
            new ProductCategory { Name = "Subscriptions", Icon = "bi-arrow-repeat", Color = "#f59e0b" },
            new ProductCategory { Name = "Consulting", Icon = "bi-people", Color = "#06b6d4" },
            new ProductCategory { Name = "Training", Icon = "bi-book", Color = "#ec4899" },
            new ProductCategory { Name = "Support", Icon = "bi-headset", Color = "#84cc16" }
        };

        public static List<string> GetCategoryNames() => Categories.Select(c => c.Name).ToList();

        public static string GetCategoryIcon(string categoryName)
        {
            return Categories.FirstOrDefault(c => c.Name == categoryName)?.Icon ?? "bi-box";
        }

        public static string GetCategoryColor(string categoryName)
        {
            return Categories.FirstOrDefault(c => c.Name == categoryName)?.Color ?? "#6b7280";
        }
    }

    public class ProductCategory
    {
        public string Name { get; set; } = string.Empty;
        public string Icon { get; set; } = string.Empty;
        public string Color { get; set; } = string.Empty;
    }
}
