// =====================================================================
// TAX RATES INDEX — Pages/Admin/TaxRates/Index.cshtml.cs
//
// 117: the list moved to each country's page (Countries → a country →
// Tax Settings). This page only forwards to the Countries list. Add, Edit
// and Detail stay here and return to the rate's country when done.
// =====================================================================

using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MerkaiTrial.Admin.Web.Pages.Admin.TaxRates
{
    public class IndexModel : PageModel
    {
        public IActionResult OnGet() => RedirectToPage("/Admin/Countries/Index");
    }
}
