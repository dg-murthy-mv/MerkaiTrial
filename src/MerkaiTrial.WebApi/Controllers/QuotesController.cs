using MerkaiTrial.Application.Commands.Quotes;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.WebApi.Controllers;

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
            await _updateQuoteHandler.Handle(tenantId, id, dto);
            return Ok(new { message = "Quote updated successfully" });
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { error = $"Quote {id} not found" });
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
            await _deleteQuoteHandler.Handle(tenantId, id);
            return Ok(new { message = "Quote deleted successfully" });
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { error = $"Quote {id} not found" });
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update quote status {QuoteId}", id);
            return StatusCode(500, new { error = "Failed to update quote status" });
        }
    }

    [HttpGet("statistics")]
    [Authorize(Policy = "Quotes.Read")]
    public async Task<ActionResult<QuoteStatisticsDto>> GetStatistics()
    {
        var tenantId = _currentUserService.GetCurrentTenantId();
        try
        {
            var statistics = await _getQuoteStatisticsHandler.Handle(tenantId);
            return Ok(statistics);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get quote statistics for tenant {TenantId}", tenantId);
            return StatusCode(500, new { error = "Failed to retrieve statistics" });
        }
    }

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
            _logger.LogError(ex, "Failed to get quote by token {Token}", token);
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update quote status by token {Token}", token);
            return StatusCode(500, new { error = "Failed to update quote status" });
        }
    }

    [HttpGet("{id:guid}/pdf")]
    [Authorize(Policy = "Quotes.Read")]
    public async Task<IActionResult> GetPdf(
    [FromRoute] Guid id,
    CancellationToken ct)
    {
        var tenantId = _currentUserService.GetCurrentTenantId();
        try
        {
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
