// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Pages/Attachments/Download.cshtml.cs
//
// NEW FILE (090b). /Attachments/Download/{id} — what every attachment's
// Download link opens (AttachmentDto.FileUrl, from AttachmentLinks).
//
// BEFORE: the link was the file's storage path, /uploads/<workspace>/...,
// which this site does not have — the files live under the WebApi — so
// every Download answered 404. Serving that folder publicly would have made
// any customer's contract downloadable by anyone with the link.
//
// NOW: signed in, or sent to the sign-in page. The file is fetched from
// GET api/attachments/{id}/download WITH THIS PERSON'S OWN TOKEN, so the
// API applies their workspace, their record visibility and their module
// permissions. Anything refused is a plain 404 here — "not yours" and
// "not there" look the same, so an id from another workspace reveals
// nothing.
//
// The file is passed on as an attachment (Content-Disposition), never shown
// as a page, with nosniff — the upload types are documents and images only,
// and this keeps the browser from second-guessing that.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace MerkaiTrial.Admin.Web.Pages.Attachments
{
    [Authorize]
    public class DownloadModel : PageModel
    {
        private readonly IApiService _api;
        private readonly ILogger<DownloadModel> _logger;

        public DownloadModel(IApiService api, ILogger<DownloadModel> logger)
        {
            _api = api;
            _logger = logger;
        }

        public async Task<IActionResult> OnGetAsync(Guid id)
        {
            ApiFile? file;
            try
            {
                file = await _api.GetFileAsync($"api/attachments/{id}/download");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not fetch attachment {AttachmentId}", id);
                return StatusCode(StatusCodes.Status502BadGateway);
            }

            if (file is null) return NotFound();

            Response.Headers["X-Content-Type-Options"] = "nosniff";
            return File(file.Content, file.ContentType, file.FileName);
        }
    }
}
