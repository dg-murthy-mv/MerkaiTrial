// =====================================================================
// ADMIN DASHBOARD - BACKEND
// Location: MerkaiTrial.Admin.Web/Pages/Admin/Index.cshtml.cs
// =====================================================================

using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Admin.Web.Pages.Admin
{
    // [Authorize(Roles = "Admin")] // Uncomment when authorization is ready
    public class IndexModel : PageModel
    {
        private readonly ILogger<IndexModel> _logger;

        public IndexModel(ILogger<IndexModel> logger)
        {
            _logger = logger;
        }

        public void OnGet()
        {
            _logger.LogInformation("Admin dashboard accessed");
        }
    }
}
