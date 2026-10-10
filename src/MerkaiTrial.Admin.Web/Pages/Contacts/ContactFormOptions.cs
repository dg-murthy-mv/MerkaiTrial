// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Contacts/ContactFormOptions.cs
//
// NEW FILE (075). The Country dropdown for Contacts/Create and
// Contacts/Edit, built in one place so the two forms cannot disagree.
//
// SOURCE OF TRUTH: the Countries table, through ICountryService — the
// same list Companies/Detail resolves names from. Before 075 the forms
// had a free 5-character text box ("TH, US, IN") and the Detail page had
// its own hardcoded table of fourteen names; anything typed outside those
// fourteen showed as the raw code, and anything typed wrongly ("Thai",
// "th ") was saved and never matched anything.
//
// A STORED VALUE THE TABLE DOES NOT KNOW IS STILL OFFERED, labelled as
// such, and selected. Otherwise opening Edit on an old contact would
// show the first country in the list, and saving — to fix a phone
// number — would quietly change the contact's country.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Countries;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace MerkaiTrial.Admin.Web.Pages.Contacts
{
    public static class ContactFormOptions
    {
        /// <summary>Never throws. A failed load still offers the current value.</summary>
        public static async Task<List<SelectListItem>> CountriesAsync(
            ICountryService countries, string? current, ILogger logger)
        {
            var items = new List<SelectListItem>();
            var selected = current?.Trim();

            try
            {
                var active = await countries.GetActiveAsync();

                foreach (var c in active
                             .Where(c => !string.IsNullOrWhiteSpace(c.Code))
                             .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
                {
                    items.Add(new SelectListItem
                    {
                        Value    = c.Code,
                        Text     = string.IsNullOrWhiteSpace(c.Name) ? c.Code : $"{c.Name} ({c.Code})",
                        Selected = string.Equals(c.Code, selected, StringComparison.OrdinalIgnoreCase)
                    });
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not load countries for the contact form");
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

        /// <summary>
        /// "IN" → "India", from the Countries table. Falls back to the code
        /// itself, and to "Not provided" for an empty value. Never throws.
        /// </summary>
        public static async Task<string> CountryNameAsync(
            ICountryService countries, string? code, ILogger logger)
        {
            if (string.IsNullOrWhiteSpace(code)) return "Not provided";

            try
            {
                var active = await countries.GetActiveAsync();
                var match  = active.FirstOrDefault(c =>
                    string.Equals(c.Code, code.Trim(), StringComparison.OrdinalIgnoreCase));

                return match is not null && !string.IsNullOrWhiteSpace(match.Name) ? match.Name : code;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not load countries to resolve {Code}", code);
                return code;
            }
        }
    }
}
