// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Companies/CompanyFormOptions.cs
//
// NEW FILE (078). The Vertical and Country dropdowns for Companies/Create
// and Companies/Edit, built in one place so the two forms cannot disagree.
//
// A STORED VALUE THE LIST DOES NOT KNOW IS STILL OFFERED, labelled as
// such, and selected. Before 078 the forms used a plain SelectList: open
// Edit on a company whose vertical or country had since been switched
// off, and the dropdown showed "-- Select --", the Required rule failed,
// and the person had to pick a different value just to fix a typo in the
// name. Saving must never quietly change what it was not asked to change.
//
// Countries come from the same helper Contacts uses
// (ContactFormOptions.CountriesAsync) — one list, one labelling rule.
//
// Neither method throws: a failed lookup still offers the current value,
// so the form stays usable and saving keeps it.
// =====================================================================

using MerkaiTrial.Admin.Web.Pages.Contacts;
using MerkaiTrial.Admin.Web.Services.Countries;
using MerkaiTrial.Admin.Web.Services.Meta;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace MerkaiTrial.Admin.Web.Pages.Companies
{
    public static class CompanyFormOptions
    {
        /// <summary>Active countries, "Thailand (TH)", plus the stored value if the table lacks it.</summary>
        public static Task<List<SelectListItem>> CountriesAsync(
            ICountryService countries, string? current, ILogger logger)
            => ContactFormOptions.CountriesAsync(countries, current, logger);

        /// <summary>Verticals by name, plus the stored value if the list lacks it.</summary>
        public static async Task<List<SelectListItem>> VerticalsAsync(
            IMetaService meta, string? current, ILogger logger)
        {
            var items    = new List<SelectListItem>();
            var selected = current?.Trim();

            try
            {
                var verticals = await meta.GetVerticalsAsync();

                foreach (var v in verticals
                             .Where(v => !string.IsNullOrWhiteSpace(v.Name))
                             .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
                {
                    items.Add(new SelectListItem
                    {
                        Value    = v.Name,
                        Text     = v.Name,
                        Selected = string.Equals(v.Name, selected, StringComparison.OrdinalIgnoreCase)
                    });
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not load verticals for the company form");
            }

            if (!string.IsNullOrEmpty(selected) &&
                !items.Any(i => string.Equals(i.Value, selected, StringComparison.OrdinalIgnoreCase)))
            {
                items.Insert(0, new SelectListItem
                {
                    Value    = selected,
                    Text     = $"{selected} (as saved)",
                    Selected = true
                });
            }

            return items;
        }
    }
}
