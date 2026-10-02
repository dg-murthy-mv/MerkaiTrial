// =====================================================================
// QuotesController.cs
// Location: MerkaiTrial.WebApi/Controllers/QuotesController.cs
//
// CHANGES (017 — approvals + record visibility)
//   ✅ Every by-id endpoint checks the quote is visible to the caller —
//      quotes follow their deal (Own / Team / All). Outside scope = 404,
//      exactly like another tenant's quote.
//        read  (GET, PDF, attachments list) → CanReadQuoteAsync — also
//              lets an approver open a quote they were asked to approve
//        write (PUT, status, DELETE, upload/delete attachment) →
//              CanWriteQuoteAsync
//   ✅ Delete attachment checks the attachment hangs on a quote the caller
//      can change (looked up from the attachment, not the URL). Before,
//      any attachment id in the tenant could be deleted this way.
//   ✅ Refusals (InvalidOperationException) → 400 with the message, so the
//      page can show "This quote needs approval before it can be sent…"
//      instead of "Failed to update quote status". Create / Update /
//      Delete / Status all do this now.
//   Public token endpoints are unchanged.
// =====================================================================

using MerkaiTrial.Application.Commands.Quotes;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.WebApi.Controllers;

/// <summary>
/// 061. Body for POST /api/quotes/{id}/send.
///
/// BaseUrl is how the API learns its own public web address. The API host
/// has no idea what hostname Admin.Web is served on, so the page passes
/// Request.Scheme + Request.Host, exactly as it already does for the
/// status endpoint. Null is fine — the handler falls back to the
/// configured Email:AppBaseUrl.
/// </summary>
public sealed record SendQuoteEmailRequest(string? BaseUrl);

[ApiController]
[Route("api/quotes")]
public class QuotesController : ControllerBase
{
    private readonly GetQuotesHandler              _getQuotesHandler;
    private readonly GetQuoteByIdHandler           _getQuoteByIdHandler;
    private readonly CreateQuoteHandler            _createQuoteHandler;
    private readonly UpdateQuoteHandler            _updateQuoteHandler;
    private readonly DeleteQuoteHandler            _deleteQuoteHandler;
    private readonly UpdateQuoteStatusHandler      _updateQuoteStatusHandler;
    private readonly GetQuoteStatisticsHandler     _getQuoteStatisticsHandler;
    // ✅ Attachment handlers
    private readonly GetQuoteAttachmentsHandler    _getAttachmentsHandler;
    private readonly UploadQuoteAttachmentHandler  _uploadAttachmentHandler;
    private readonly DeleteQuoteAttachmentHandler  _deleteAttachmentHandler;
    private readonly GenerateQuotePdfHandler _generatePdf;
    private readonly GetQuoteByTokenHandler _getByTokenHandler;
    private readonly SendQuoteEmailHandler         _sendQuoteEmailHandler;   // 061
    private readonly QuoteApprovalEngine           _access;
    private readonly ICurrentUserService           _currentUserService;
    private readonly ILogger<QuotesController>     _logger;

    public QuotesController(
        GetQuotesHandler              getQuotesHandler,
        GetQuoteByIdHandler           getQuoteByIdHandler,
        CreateQuoteHandler            createQuoteHandler,
        UpdateQuoteHandler            updateQuoteHandler,
        DeleteQuoteHandler            deleteQuoteHandler,
        UpdateQuoteStatusHandler      updateQuoteStatusHandler,
        GetQuoteStatisticsHandler     getQuoteStatisticsHandler,
        GetQuoteAttachmentsHandler    getAttachmentsHandler,
        UploadQuoteAttachmentHandler  uploadAttachmentHandler,
        DeleteQuoteAttachmentHandler  deleteAttachmentHandler,
         GenerateQuotePdfHandler      generatePdf,
            GetQuoteByTokenHandler       getByTokenHandler,
        SendQuoteEmailHandler         sendQuoteEmailHandler,              // 061
        QuoteApprovalEngine           access,
        ICurrentUserService            currentUserService,
        ILogger<QuotesController>     logger)
    {
        _getQuotesHandler          = getQuotesHandler;
        _getQuoteByIdHandler       = getQuoteByIdHandler;
        _createQuoteHandler        = createQuoteHandler;
        _updateQuoteHandler        = updateQuoteHandler;
        _deleteQuoteHandler        = deleteQuoteHandler;
        _updateQuoteStatusHandler  = updateQuoteStatusHandler;
        _getQuoteStatisticsHandler = getQuoteStatisticsHandler;
        _getAttachmentsHandler     = getAttachmentsHandler;
        _uploadAttachmentHandler   = uploadAttachmentHandler;
        _deleteAttachmentHandler   = deleteAttachmentHandler;
        _getByTokenHandler         = getByTokenHandler;
        _sendQuoteEmailHandler     = sendQuoteEmailHandler;   // 061
        _access                    = access;
        _generatePdf = generatePdf;
        _currentUserService        = currentUserService;
        _logger                    = logger;
    }

    // ── QUOTE CRUD ────────────────────────────────────────────────────

    [HttpGet]
    [Authorize(Policy = "Quotes.Read")]
    public async Task<ActionResult<List<QuoteListItem>>> GetAll(
        [FromQuery] Guid? dealId = null,
        [FromQuery] string? status = null,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null)
    {
        var tenantId = _currentUserService.GetCurrentTenantId();
        try
        {
            var quotes = await _getQuotesHandler.Handle(tenantId, dealId, status, fromDate, toDate);
            return Ok(quotes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get quotes for tenant {TenantId}", tenantId);
            return StatusCode(500, new { error = "Failed to retrieve quotes" });
        }
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = "Quotes.Read")]
    public async Task<ActionResult<QuoteDto>> GetById(
        [FromRoute] Guid id)
    {
        var tenantId = _currentUserService.GetCurrentTenantId();
        try
        {
            if (!await _access.CanReadQuoteAsync(tenantId, id))
                return NotFound(new { error = $"Quote {id} not found" });

            var quote = await _getQuoteByIdHandler.Handle(tenantId, id);
            return Ok(quote);
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { error = $"Quote {id} not found" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get quote {QuoteId}", id);
            return StatusCode(500, new { error = "Failed to retrieve quote" });
        }
    }

    [HttpPost]
    [Authorize(Policy = "Quotes.Create")]
    public async Task<ActionResult<QuoteDto>> Create([FromBody] CreateQuoteDto dto)
    {
        try
        {
            // Server-resolved identity always wins over whatever the client sent in the body.
            dto.TenantId = _currentUserService.GetCurrentTenantId();

            var quote = await _createQuoteHandler.Handle(dto);
            return CreatedAtAction(nameof(GetById),
                new { id = quote.Id, tenantId = dto.TenantId }, quote);
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { error = "Deal not found" });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create quote");
            return StatusCode(500, new { error = "Failed to create quote" });
        }
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = "Quotes.Update")]
    public async Task<IActionResult> Update(
        [FromRoute] Guid id, [FromBody] UpdateQuoteDto dto)
    {
        var tenantId = _currentUserService.GetCurrentTenantId();
        try
        {
            if (!await _access.CanWriteQuoteAsync(tenantId, id))
                return NotFound(new { error = $"Quote {id} not found" });

            await _updateQuoteHandler.Handle(tenantId, id, dto);
            return Ok(new { message = "Quote updated successfully" });
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { error = $"Quote {id} not found" });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update quote {QuoteId}", id);
            return StatusCode(500, new { error = "Failed to update quote" });
        }
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "Quotes.Delete")]
    public async Task<IActionResult> Delete(
        [FromRoute] Guid id)
    {
        var tenantId = _currentUserService.GetCurrentTenantId();
        try
        {
            if (!await _access.CanWriteQuoteAsync(tenantId, id))
                return NotFound(new { error = $"Quote {id} not found" });

            await _deleteQuoteHandler.Handle(tenantId, id);
            return Ok(new { message = "Quote deleted successfully" });
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { error = $"Quote {id} not found" });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete quote {QuoteId}", id);
            return StatusCode(500, new { error = "Failed to delete quote" });
        }
    }

    [HttpPut("{id:guid}/status")]
    [Authorize(Policy = "Quotes.Update")]
    public async Task<IActionResult> UpdateStatus(
        [FromRoute] Guid id, [FromBody] UpdateQuoteStatusDto dto)
    {
        var tenantId = _currentUserService.GetCurrentTenantId();
        try
        {
            if (!await _access.CanWriteQuoteAsync(tenantId, id))
                return NotFound(new { error = $"Quote {id} not found" });

            await _updateQuoteStatusHandler.Handle(tenantId, id, dto);
            return Ok(new { message = "Quote status updated successfully" });
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { error = $"Quote {id} not found" });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update quote status {QuoteId}", id);
            return StatusCode(500, new { error = "Failed to update quote status" });
        }
    }

    /// <summary>
    /// GET /api/quotes/statistics — optionally for one period.
    ///
    /// 043a: `from` and `toExclusive` bound the quote's ISSUE DATE, matching
    /// the list endpoint above, so the dashboard's "12 quotes this year" and
    /// the quotes list can never disagree about which twelve those are.
    ///
    /// HALF-OPEN: `toExclusive` is the first instant NOT included. Omit both
    /// for all time, exactly as before.
    ///
    /// IssueDateUtc is a DATE-ONLY column since 034 — it holds the picked
    /// calendar date at midnight with no offset — so the caller sends the
    /// fiscal service's DateOnlyRange, NOT its timezone-shifted timestamp
    /// range. Shifting it moves every quote issued on the first or last day
    /// of a financial year into the wrong one.
    /// </summary>
    [HttpGet("statistics")]
    [Authorize(Policy = "Quotes.Read")]
    public async Task<ActionResult<QuoteStatisticsDto>> GetStatistics(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? toExclusive = null)
    {
        var tenantId = _currentUserService.GetCurrentTenantId();
        try
        {
            var statistics = await _getQuoteStatisticsHandler.Handle(
                tenantId, AsUtc(from), AsUtc(toExclusive));

            return Ok(statistics);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get quote statistics for tenant {TenantId}", tenantId);
            return StatusCode(500, new { error = "Failed to retrieve statistics" });
        }
    }

    /// <summary>
    /// 043a. Whatever the model binder made of the string, treat it as the
    /// UTC instant the caller meant — "…Z" binds to Kind=Utc, a bare
    /// timestamp to Unspecified, and some configurations to Local. Comparing
    /// a Local instant against a UTC column shifts every boundary record by
    /// the server's offset, invisibly. See the same helper in
    /// LeadsController for why it is repeated rather than shared.
    /// </summary>
    private static DateTime? AsUtc(DateTime? value) => value is null
        ? null
        : value.Value.Kind switch
        {
            DateTimeKind.Utc   => value,
            DateTimeKind.Local => value.Value.ToUniversalTime(),
            _                  => DateTime.SpecifyKind(value.Value, DateTimeKind.Utc)
        };

    // ── ✅ ATTACHMENTS ────────────────────────────────────────────────

    // GET api/quotes/{id}/attachments?tenantId={guid}
    [HttpGet("{id:guid}/attachments")]
    [Authorize(Policy = "Quotes.Read")]
    public async Task<IActionResult> GetAttachments(
        Guid id)
    {
        var tenantId = _currentUserService.GetCurrentTenantId();
        try
        {
            // Not visible → empty list, same as a quote with no files.
            if (!await _access.CanReadQuoteAsync(tenantId, id))
                return Ok(new List<AttachmentDto>());

            var result = await _getAttachmentsHandler.HandleAsync(tenantId, id);
            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get attachments for quote {QuoteId}", id);
            return StatusCode(500, new { error = "Failed to retrieve attachments" });
        }
    }

    // POST api/quotes/{id}/attachments?tenantId={guid}
    // Content-Type: multipart/form-data  field: "file"
    [HttpPost("{id:guid}/attachments")]
    [Authorize(Policy = "Quotes.Update")]
    [RequestSizeLimit(10 * 1024 * 1024)]
    public async Task<IActionResult> UploadAttachment(
        Guid id,
        IFormFile file, CancellationToken ct)
    {
        var tenantId = _currentUserService.GetCurrentTenantId();
        try
        {
            if (file == null || file.Length == 0)
                return BadRequest(new { error = "No file provided" });

            if (!await _access.CanWriteQuoteAsync(tenantId, id, ct))
                return NotFound(new { error = $"Quote {id} not found" });

            var result = await _uploadAttachmentHandler.HandleAsync(tenantId, id, file, ct);
            return Ok(result);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { error = $"Quote {id} not found" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upload attachment for quote {QuoteId}", id);
            return StatusCode(500, new { error = "Failed to upload attachment" });
        }
    }

    // DELETE api/quotes/{id}/attachments/{attachmentId}?tenantId={guid}
    [HttpDelete("{id:guid}/attachments/{attachmentId:guid}")]
    [Authorize(Policy = "Quotes.Delete")]
    public async Task<IActionResult> DeleteAttachment(
        Guid id, Guid attachmentId, CancellationToken ct)
    {
        var tenantId = _currentUserService.GetCurrentTenantId();
        try
        {
            // Checked from the attachment's own quote, not the {id} in the
            // URL — the page's client doesn't always send the real quote id.
            if (!await _access.CanWriteQuoteAttachmentAsync(tenantId, attachmentId, ct))
                return NotFound(new { error = $"Attachment {attachmentId} not found" });

            await _deleteAttachmentHandler.HandleAsync(tenantId, attachmentId, ct);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { error = $"Attachment {attachmentId} not found" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete attachment {AttachmentId}", attachmentId);
            return StatusCode(500, new { error = "Failed to delete attachment" });
        }
    }

    /// <summary>
    /// 061. Emails the quote to the customer again.
    ///
    /// Does NOT change the status — resending is not a new event in a
    /// quote's life. The FIRST email goes out automatically when the quote
    /// moves to Sent, queued inside that transaction by
    /// UpdateQuoteStatusHandler.
    ///
    /// A refusal that the rep can act on ("this contact has no email
    /// address") comes back as 400 with that sentence, the same shape the
    /// quote page already renders for every other refusal here.
    /// </summary>
    [HttpPost("{id:guid}/send")]
    [Authorize(Policy = "Quotes.Update")]
    public async Task<IActionResult> SendToCustomer(
        [FromRoute] Guid id,
        [FromBody] SendQuoteEmailRequest? request,
        CancellationToken ct)
    {
        var tenantId = _currentUserService.GetCurrentTenantId();
        try
        {
            if (!await _access.CanWriteQuoteAsync(tenantId, id, ct))
                return NotFound(new { error = $"Quote {id} not found" });

            string? sentBy = null;
            try
            {
                var user = await _currentUserService.GetCurrentUserAsync();
                sentBy = user?.FullName;
            }
            catch
            {
                // Who pressed it is for the audit row, not for the send.
                // Never fail an email because the name could not be read.
            }

            var result = await _sendQuoteEmailHandler.HandleAsync(
                tenantId, id, request?.BaseUrl, sentBy, ct);

            if (!result.Queued)
                return BadRequest(new { error = result.Reason ?? "The quote could not be emailed." });

            return Ok(new { message = "Quote email queued" });
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { error = $"Quote {id} not found" });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to email quote {QuoteId}", id);
            return StatusCode(500, new { error = "Failed to email the quote" });
        }
    }

    [HttpGet("public/{token}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetByToken(string token, CancellationToken ct)
    {
        try
        {
            var quote = await _getByTokenHandler.HandleAsync(token, ct);
            if (quote == null) return NotFound();
            return Ok(quote);
        }
        catch (Exception ex)
        {
            // 061: the token is NOT logged. It is the ENTIRE security of
            // the /q/{token} URL — anyone with log access could paste it
            // into a browser, open the customer's quote and accept it as
            // them. Same rule as the phone numbers in WhatsApp sending.
            _logger.LogError(ex, "Failed to get quote by token {Token}", Mask(token));
            return StatusCode(500);
        }
    }

    [HttpPut("public/{token}/status")]
    [AllowAnonymous]
    public async Task<IActionResult> UpdateStatusByToken(
    string token,
    [FromBody] UpdateQuoteStatusDto dto,
    CancellationToken ct)
    {
        try
        {
            // ✅ Resolve token → QuoteDto (handler does the DB lookup)
            var quote = await _getByTokenHandler.HandleAsync(token, ct);
            if (quote == null)
                return NotFound(new { error = "Quote not found or link is invalid" });

            // Guard — only allow customer to set Accepted or Rejected
            if (dto.Status is not ("Accepted" or "Rejected"))
                return BadRequest(new { error = "Invalid status. Allowed: Accepted, Rejected" });

            // Guard — only transition from Sent/Viewed
            if (quote.Status is not ("Sent" or "Viewed"))
                return Ok(new { message = "Quote already decided — no change needed" });

            // 061: AND the quote must not have expired.
            //
            // This check did not exist. The guards above cover the target
            // status and the current status, but nothing looked at the
            // date — so a quote that lapsed three months ago could still be
            // accepted, at a price that is no longer on offer, by anyone
            // who still had the link. The public page hides the buttons
            // once a quote expires, but a hidden button is not a control
            // and this endpoint is reachable directly.
            if (quote.ExpiresAtUtc != default
                && quote.ExpiresAtUtc.Year > 1900
                && quote.ExpiresAtUtc < DateTime.UtcNow)
            {
                return BadRequest(new
                {
                    error = "This quote has expired. Please ask for an updated quote."
                });
            }

            // ✅ Delegate to existing handler — deals with deal stage transition too
            await _updateQuoteStatusHandler.Handle(
                quote.TenantId,
                quote.Id,
                new UpdateQuoteStatusDto
                {
                    Status = dto.Status,
                    UpdatedBy = "Customer"
                });

            _logger.LogInformation(
                "Quote {QuoteId} → {Status} by customer via public token",
                quote.Id, dto.Status);

            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception ex)
        {
            // 061: masked. See GetByToken above.
            _logger.LogError(ex, "Failed to update quote status by token {Token}", Mask(token));
            return StatusCode(500, new { error = "Failed to update quote status" });
        }
    }

    /// <summary>
    /// 061. The last four characters of a public quote token, for the log.
    /// Enough to match a row against a support question; useless to anyone
    /// who wants to open the quote. A short or empty token degrades to
    /// "****" rather than leaking a prefix of a short one.
    /// </summary>
    private static string Mask(string? token)
        => string.IsNullOrWhiteSpace(token) || token.Length < 8
            ? "****"
            : "****" + token[^4..];

    [HttpGet("{id:guid}/pdf")]
    [Authorize(Policy = "Quotes.Read")]
    public async Task<IActionResult> GetPdf(
    [FromRoute] Guid id,
    CancellationToken ct)
    {
        var tenantId = _currentUserService.GetCurrentTenantId();
        try
        {
            if (!await _access.CanReadQuoteAsync(tenantId, id, ct))
                return NotFound(new { error = $"Quote {id} not found" });

            var pdfBytes = await _generatePdf.HandleAsync(tenantId, id, ct);

            // Resolve quote number for a clean download filename
            string filename;
            try
            {
                var quote = await _getQuoteByIdHandler.Handle(tenantId, id);
                filename = $"{quote.Number}.pdf";
            }
            catch
            {
                filename = $"quote-{id.ToString()[..8]}.pdf";
            }

            Response.Headers["Content-Disposition"] =
                $"attachment; filename=\"{filename}\"";

            return File(pdfBytes, "application/pdf", filename);
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { error = $"Quote {id} not found" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate PDF for quote {QuoteId}", id);
            return StatusCode(500, new { error = "Failed to generate quote PDF" });
        }
    }
}
