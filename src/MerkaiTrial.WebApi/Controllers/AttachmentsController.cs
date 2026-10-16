// =====================================================================
// FILE: MerkaiTrial.WebApi/Controllers/AttachmentsController.cs
//
// NEW FILE (090b). GET api/attachments/{id}/download — the one way an
// attachment's file leaves the server. See AttachmentDownloadHandler for
// why the old /uploads/... links could never work, and why they must not
// be made to work as public files.
//
// TWO CHECKS, BOTH ANSWERING 404
//   1. The handler: this workspace's attachment, not deleted, on a record
//      this person may see (record visibility).
//   2. Here: the module's read permission for that record type — a person
//      who cannot open deals cannot download a deal's file either.
//   Both refusals are 404, the same as a file that does not exist, so an
//   id from another workspace tells the caller nothing.
//
// The tenant comes from the signed token, never the query string.
// =====================================================================

using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Commands.Attachments;
using MerkaiTrial.Application.Configuration;
using MerkaiTrial.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.WebApi.Controllers
{
    [ApiController]
    [Route("api/attachments")]
    [Authorize]
    public class AttachmentsController : ControllerBase
    {
        private readonly GetAttachmentDownloadHandler _download;
        private readonly IAuthorizationService _authorization;
        private readonly ICurrentUserService _currentUserService;
        private readonly ILogger<AttachmentsController> _logger;

        public AttachmentsController(
            GetAttachmentDownloadHandler download,
            IAuthorizationService authorization,
            ICurrentUserService currentUserService,
            ILogger<AttachmentsController> logger)
        {
            _download           = download;
            _authorization      = authorization;
            _currentUserService = currentUserService;
            _logger             = logger;
        }

        /// <summary>
        /// GET api/attachments/{id}/download — the file, as an attachment
        /// (never rendered by the browser as a page), or 404.
        /// </summary>
        [HttpGet("{id:guid}/download")]
        [ProducesResponseType(200)]
        [ProducesResponseType(404)]
        public async Task<IActionResult> Download(Guid id, CancellationToken ct)
        {
            var tenantId = _currentUserService.GetCurrentTenantId();
            AttachmentFile? file = null;

            try
            {
                file = await _download.Handle(tenantId, id, ct);
                if (file is null) return NotFoundResult();

                var type = CustomFieldEntityTypes.Normalise(file.EntityType);
                var allowed = type is not null && (await _authorization.AuthorizeAsync(
                    User, $"{CustomFieldEntityTypes.ModuleFor(type)}.{Actions.Read}")).Succeeded;

                if (!allowed)
                {
                    await file.Content.DisposeAsync();
                    return NotFoundResult();
                }

                // The types are already limited to documents and images on
                // upload; nosniff stops a browser second-guessing that.
                Response.Headers["X-Content-Type-Options"] = "nosniff";

                // FileStreamResult disposes the stream when it is done.
                return File(file.Content, file.ContentType, file.FileName);
            }
            catch (Exception ex)
            {
                if (file is not null) await file.Content.DisposeAsync();
                _logger.LogError(ex, "Failed to download attachment {AttachmentId}", id);
                return StatusCode(500, new { error = "The file could not be opened just now." });
            }
        }

        private IActionResult NotFoundResult() => NotFound(new { error = "That file is not available." });
    }
}
