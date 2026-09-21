// =====================================================================
// FILE: MerkaiTrial.WebApi/Controllers/InvoicesController.cs
// PURPOSE: Invoice API Controller - MATCHES YOUR EXISTING HANDLERS
// FIXED: Works with your mixed DTO/Command pattern
//
// CHANGES (017 — record visibility)
//   ✅ Every by-id endpoint (GET, PUT, status, payments, DELETE, PDF)
//      checks the invoice is visible to the caller — invoices follow their
//      deal. Outside scope = 404, like another tenant's invoice.
//   ✅ Create / from-quote: visibility is checked in the handlers; a
//      refusal ("Link this invoice to a deal") now comes back as 400 with
//      the message, not 500.
//   ✅ Status: InvoiceStatusRules refusals ("Record the payment instead")
//      were InvalidOperationException, which this action did not catch —
//      the user got "Failed to update invoice status". Now 400 + message.
//
// CHANGES (018 — invoice workflow)
//   GET  api/invoices/{id}/workflow                     Invoices.Read   what can happen next
//   POST api/invoices/{id}/issue                        Invoices.Update draft → INV-nnnn, locked
//   POST api/invoices/{id}/void            {reason}     Invoices.Update issued → Void
//   POST api/invoices/{id}/payments/{pid}/reverse {reason} Invoices.Update take a payment back out
//   ✅ Delete refusals (issued invoices can't be deleted) → 400 + message.
// =====================================================================

using MerkaiTrial.Application.Commands.Invoices;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Queries.Invoices;
using MerkaiTrial.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.WebApi.Controllers
{
    /// <summary>
    /// Invoice management API - Create, Read, Update, Delete invoices
    /// </summary>
    [ApiController]
    [Route("api/invoices")]
    [Produces("application/json")]
    public class InvoicesController : ControllerBase
    {
        // ==================== DEPENDENCIES ====================

        // Query Handlers
        private readonly GetInvoicesHandler _getInvoices;
        private readonly GetInvoiceByIdHandler _getById;
        private readonly GetInvoiceStatisticsHandler _getStats;

        private readonly GenerateInvoicePdfHandler _generatePdf;

        // Command Handlers
        private readonly CreateInvoiceHandler _createInvoice;
        private readonly CreateInvoiceFromQuoteHandler _createFromQuote;
        private readonly UpdateInvoiceHandler _updateInvoice;
        private readonly UpdateInvoiceStatusHandler _updateStatus;
        private readonly AddPaymentHandler _addPayment;
        private readonly DeleteInvoiceHandler _deleteInvoice;

        private readonly InvoiceAccessHandler _access;
        private readonly IssueInvoiceHandler _issue;
        private readonly VoidInvoiceHandler _void;
        private readonly ReversePaymentHandler _reversePayment;
        private readonly GetInvoiceWorkflowHandler _workflow;
        private readonly ICurrentUserService _currentUserService;
        private readonly ILogger<InvoicesController> _logger;

        // ==================== CONSTRUCTOR ====================

        public InvoicesController(
            // Queries
            GetInvoicesHandler getInvoices,
            GetInvoiceByIdHandler getById,
            GetInvoiceStatisticsHandler getStats,
            // Commands
            CreateInvoiceHandler createInvoice,
            CreateInvoiceFromQuoteHandler createFromQuote,
            UpdateInvoiceHandler updateInvoice,
            UpdateInvoiceStatusHandler updateStatus,
            AddPaymentHandler addPayment,
            DeleteInvoiceHandler deleteInvoice,
            GenerateInvoicePdfHandler generatePdf,
            InvoiceAccessHandler access,
            IssueInvoiceHandler issue,
            VoidInvoiceHandler voidInvoice,
            ReversePaymentHandler reversePayment,
            GetInvoiceWorkflowHandler workflow,
            ICurrentUserService currentUserService,
            ILogger<InvoicesController> logger)
        {
            _getInvoices = getInvoices;
            _getById = getById;
            _getStats = getStats;
            _createInvoice = createInvoice;
            _createFromQuote = createFromQuote;
            _updateInvoice = updateInvoice;
            _updateStatus = updateStatus;
            _addPayment = addPayment;
            _deleteInvoice = deleteInvoice;
            _generatePdf = generatePdf;
            _access = access;
            _issue = issue;
            _void = voidInvoice;
            _reversePayment = reversePayment;
            _workflow = workflow;
            _currentUserService = currentUserService;
            _logger = logger;
        }

        // ==================== QUERIES (GET) ====================

        /// <summary>
        /// Get all invoices with optional filters
        /// </summary>
        [HttpGet]
        [Authorize(Policy = "Invoices.Read")]
        [ProducesResponseType(typeof(List<InvoiceListItem>), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetAll(
            [FromQuery] Guid? quoteId = null,
            [FromQuery] Guid? dealId = null,
            [FromQuery] string? status = null,
            [FromQuery] DateTime? fromDate = null,
            [FromQuery] DateTime? toDate = null,
            CancellationToken cancellationToken = default)
        {
            var tenantId = _currentUserService.GetCurrentTenantId();
            try
            {
                var query = new GetInvoicesQuery
                {
                    TenantId = tenantId,
                    QuoteId = quoteId,
                    DealId = dealId,
                    Status = status,
                    FromDate = fromDate,
                    ToDate = toDate
                };

                var invoices = await _getInvoices.Handle(query, cancellationToken);
                _logger.LogInformation("Retrieved {Count} invoices", invoices.Count);

                return Ok(invoices);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting invoices");
                return StatusCode(500, new { error = "Failed to retrieve invoices" });
            }
        }

        /// <summary>
        /// Get invoice by ID
        /// </summary>
        [HttpGet("{id}")]
        [Authorize(Policy = "Invoices.Read")]
        [ProducesResponseType(typeof(InvoiceDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> GetById(
            Guid id,
            CancellationToken cancellationToken = default)
        {
            var tenantId = _currentUserService.GetCurrentTenantId();
            try
            {
                if (!await _access.CanSeeInvoiceAsync(tenantId, id, cancellationToken))
                    return NotFound(new { error = $"Invoice {id} not found" });

                var query = new GetInvoiceByIdQuery
                {
                    TenantId = tenantId,
                    Id = id
                };

                var invoice = await _getById.Handle(query, cancellationToken);
                return Ok(invoice);
            }
            catch (KeyNotFoundException)
            {
                return NotFound(new { error = $"Invoice {id} not found" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting invoice {Id}", id);
                return StatusCode(500, new { error = "Failed to retrieve invoice" });
            }
        }

        /// <summary>
        /// Get invoice statistics
        /// </summary>
        [HttpGet("statistics")]
        [Authorize(Policy = "Invoices.Read")]
        [ProducesResponseType(typeof(InvoiceStatisticsDto), StatusCodes.Status200OK)]
        public async Task<IActionResult> GetStatistics(
            CancellationToken cancellationToken = default)
        {
            var tenantId = _currentUserService.GetCurrentTenantId();
            try
            {
                var query = new GetInvoiceStatisticsQuery { TenantId = tenantId };
                var stats = await _getStats.Handle(query, cancellationToken);
                return Ok(stats);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting statistics");
                return StatusCode(500, new { error = "Failed to retrieve statistics" });
            }
        }

        // ==================== COMMANDS (POST, PUT, PATCH, DELETE) ====================

        /// <summary>
        /// Create invoice manually
        /// </summary>
        [HttpPost]
        [Authorize(Policy = "Invoices.Create")]
        [ProducesResponseType(typeof(InvoiceDto), StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<IActionResult> Create([FromBody] CreateInvoiceDto dto)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                // Server-resolved identity always wins over whatever the client sent in the body.
                dto.TenantId = _currentUserService.GetCurrentTenantId();

                // (018) A MANUAL invoice is never "from a quote" — that path
                // (POST from-quote) checks the quote is accepted, copies its
                // lines and skips the approval limits. Accepting a QuoteId here
                // would let any lines ride on a quote's approval.
                dto.QuoteId = null;

                // ✅ Handler signature: Handle(CreateInvoiceDto dto)
                var invoice = await _createInvoice.Handle(dto);

                _logger.LogInformation("Created invoice {Number}", invoice.Number);

                return CreatedAtAction(
                    nameof(GetById),
                    new { id = invoice.Id, tenantId = invoice.TenantId },
                    invoice);
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
                _logger.LogError(ex, "Error creating invoice");
                return StatusCode(500, new { error = "Failed to create invoice" });
            }
        }

        /// <summary>
        /// Create invoice from accepted quote
        /// </summary>
        [HttpPost("from-quote")]
        [Authorize(Policy = "Invoices.Create")]
        [ProducesResponseType(typeof(InvoiceDto), StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> CreateFromQuote([FromBody] CreateInvoiceFromQuoteDto dto)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                // Server-resolved identity always wins over whatever the client sent in the body.
                dto.TenantId = _currentUserService.GetCurrentTenantId();

                // ✅ Handler signature: Handle(CreateInvoiceFromQuoteDto dto)
                var invoice = await _createFromQuote.Handle(dto);

                _logger.LogInformation("Created invoice {Number} from quote {QuoteId}",
                    invoice.Number, dto.QuoteId);

                return CreatedAtAction(
                    nameof(GetById),
                    new { id = invoice.Id, tenantId = invoice.TenantId },
                    invoice);
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating invoice from quote");
                return StatusCode(500, new { error = "Failed to create invoice from quote" });
            }
        }

        /// <summary>
        /// Update invoice (Draft only)
        /// </summary>
        [HttpPut("{id}")]
        [Authorize(Policy = "Invoices.Update")]
        [ProducesResponseType(typeof(InvoiceDto), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> Update(
            Guid id,
            [FromBody] UpdateInvoiceDto dto,
            CancellationToken cancellationToken = default)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                // Server-resolved identity always wins over whatever the client sent in the body.
                dto.TenantId = _currentUserService.GetCurrentTenantId();

                if (!await _access.CanSeeInvoiceAsync(dto.TenantId, id, cancellationToken))
                    return NotFound(new { error = $"Invoice {id} not found" });

                // ✅ Handler signature: Handle(UpdateInvoiceCommand command, CancellationToken ct)
                // Map DTO → Command
                var command = new UpdateInvoiceCommand
                {
                    TenantId = dto.TenantId,
                    Id = id,
                    DueDateUtc = dto.DueDateUtc,
                    Notes = dto.Notes,
                    Lines = dto.Lines,
                    UpdatedBy = dto.UpdatedBy
                };

                var invoice = await _updateInvoice.Handle(command, cancellationToken);

                _logger.LogInformation("Updated invoice {Id}", id);

                return Ok(invoice);
            }
            catch (KeyNotFoundException)
            {
                return NotFound(new { error = $"Invoice {id} not found" });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating invoice {Id}", id);
                return StatusCode(500, new { error = "Failed to update invoice" });
            }
        }

        /// <summary>
        /// Update invoice status
        /// </summary>
        // ✅ FIXED: Changed from [HttpPatch] to [HttpPut] to match service PutAsync call
        //          Changed body from raw string to UpdateInvoiceStatusDto
        [HttpPut("{id}/status")]
        [Authorize(Policy = "Invoices.Update")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> UpdateStatus(
            Guid id,
            [FromBody] UpdateInvoiceStatusDto dto)
        {
            var tenantId = _currentUserService.GetCurrentTenantId();
            try
            {
                if (!await _access.CanSeeInvoiceAsync(tenantId, id))
                    return NotFound(new { error = $"Invoice {id} not found" });

                dto.Id       = id;
                dto.TenantId = tenantId;
                if (string.IsNullOrEmpty(dto.UpdatedBy))
                    dto.UpdatedBy = "System";

                await _updateStatus.Handle(tenantId, id, dto);

                _logger.LogInformation("Updated invoice {Id} status to {Status}", id, dto.Status);

                return NoContent();
            }
            catch (KeyNotFoundException)
            {
                return NotFound(new { error = $"Invoice {id} not found" });
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
                _logger.LogError(ex, "Error updating invoice status");
                return StatusCode(500, new { error = "Failed to update invoice status" });
            }
        }

        /// <summary>
        /// Add payment to invoice
        /// </summary>
        [HttpPost("{id}/payments")]
        [Authorize(Policy = "Invoices.Update")]
        [ProducesResponseType(typeof(PaymentDto), StatusCodes.Status201Created)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> AddPayment(
            Guid id,
            [FromBody] CreatePaymentDto dto)
        {
            try
            {
                if (!ModelState.IsValid)
                    return BadRequest(ModelState);

                // Ensure invoice ID matches
                dto.InvoiceId = id;

                // Server-resolved identity always wins over whatever the client sent in the body.
                dto.TenantId = _currentUserService.GetCurrentTenantId();

                if (!await _access.CanSeeInvoiceAsync(dto.TenantId, id))
                    return NotFound(new { error = $"Invoice {id} not found" });

                // ✅ Handler signature: Handle(CreatePaymentDto dto)
                var payment = await _addPayment.Handle(dto);

                _logger.LogInformation("Added payment {Amount} to invoice {Id}", dto.Amount, id);

                return CreatedAtAction(
                    nameof(GetById),
                    new { id = id, tenantId = dto.TenantId },
                    payment);
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding payment");
                return StatusCode(500, new { error = "Failed to add payment" });
            }
        }

        /// <summary>
        /// Delete invoice (soft delete)
        /// </summary>
        [HttpDelete("{id}")]
        [Authorize(Policy = "Invoices.Delete")]
        [ProducesResponseType(StatusCodes.Status204NoContent)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        public async Task<IActionResult> Delete(
            Guid id,
            [FromQuery] string? deletedBy = null)
        {
            var tenantId = _currentUserService.GetCurrentTenantId();
            try
            {
                if (!await _access.CanSeeInvoiceAsync(tenantId, id))
                    return NotFound(new { error = $"Invoice {id} not found" });

                // ✅ Handler signature: Handle(Guid tenantId, Guid invoiceId, string? deletedBy)
                await _deleteInvoice.Handle(tenantId, id, deletedBy);

                _logger.LogInformation("Deleted invoice {Id}", id);

                return NoContent();
            }
            catch (KeyNotFoundException)
            {
                return NotFound(new { error = $"Invoice {id} not found" });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting invoice {Id}", id);
                return StatusCode(500, new { error = "Failed to delete invoice" });
            }
        }

        // ==================== WORKFLOW (018) ====================

        [HttpGet("{id:guid}/workflow")]
        [Authorize(Policy = "Invoices.Read")]
        [ProducesResponseType(typeof(InvoiceWorkflowDto), StatusCodes.Status200OK)]
        public Task<IActionResult> GetWorkflow(Guid id, CancellationToken ct)
            => RunGuarded(id, async tenantId => Ok(await _workflow.Handle(tenantId, id, ct)), "reading invoice workflow", ct);

        [HttpPost("{id:guid}/issue")]
        [Authorize(Policy = "Invoices.Update")]
        public Task<IActionResult> Issue(Guid id, CancellationToken ct)
            => RunGuarded(id, async tenantId =>
            {
                var number = await _issue.Handle(tenantId, id, ct);
                return Ok(new { number });
            }, "issuing invoice", ct);

        [HttpPost("{id:guid}/void")]
        [Authorize(Policy = "Invoices.Update")]
        public Task<IActionResult> Void(Guid id, [FromBody] VoidInvoiceDto dto, CancellationToken ct)
            => RunGuarded(id, async tenantId =>
            {
                await _void.Handle(tenantId, id, dto?.Reason, ct);
                return Ok();
            }, "voiding invoice", ct);

        [HttpPost("{id:guid}/payments/{paymentId:guid}/reverse")]
        [Authorize(Policy = "Invoices.Update")]
        public Task<IActionResult> ReversePayment(Guid id, Guid paymentId, [FromBody] ReversePaymentDto dto, CancellationToken ct)
            => RunGuarded(id, async tenantId =>
            {
                await _reversePayment.Handle(tenantId, id, paymentId, dto?.Reason, ct);
                return Ok();
            }, "reversing payment", ct);

        /// <summary>Visibility check + the usual exception → status mapping.</summary>
        private async Task<IActionResult> RunGuarded(
            Guid id, Func<Guid, Task<IActionResult>> body, string what, CancellationToken ct)
        {
            var tenantId = _currentUserService.GetCurrentTenantId();
            try
            {
                if (!await _access.CanSeeInvoiceAsync(tenantId, id, ct))
                    return NotFound(new { error = $"Invoice {id} not found" });

                return await body(tenantId);
            }
            catch (KeyNotFoundException ex)      { return NotFound(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error {What} {Id}", what, id);
                return StatusCode(500, new { error = "Something went wrong. Please try again." });
            }
        }

        // GET: api/invoices/{id}/pdf?tenantId={guid}
        // Returns: application/pdf stream — save as INV-0001.pdf
        [HttpGet("{id}/pdf")]
        [Authorize(Policy = "Invoices.Read")]
        public async Task<IActionResult> GetPdf(
            Guid id,
            CancellationToken ct)
        {
            var tenantId = _currentUserService.GetCurrentTenantId();
            try
            {
                if (!await _access.CanSeeInvoiceAsync(tenantId, id, ct))
                    return NotFound(new { error = $"Invoice {id} not found" });

                var pdfBytes = await _generatePdf.HandleAsync(tenantId, id, ct);

                // Suggest filename to browser / WhatsApp share sheet
                var invoiceNumber = await _getInvoiceNumberForFilename(tenantId, id, ct);
                var filename = $"{invoiceNumber}.pdf";

                Response.Headers["Content-Disposition"] =
                    $"attachment; filename=\"{filename}\"";

                return File(pdfBytes, "application/pdf", filename);
            }
            catch (KeyNotFoundException)
            {
                return NotFound(new { error = $"Invoice {id} not found" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to generate PDF for invoice {InvoiceId}", id);
                return StatusCode(500, new { error = "Failed to generate invoice PDF" });
            }
        }

        // ✅ Helper — avoids loading full invoice just for filename
        private async Task<string> _getInvoiceNumberForFilename(
            Guid tenantId, Guid invoiceId, CancellationToken ct)
        {
            try
            {
                // Re-use GetByIdHandler query but only need Number
                var invoice = await _getById.Handle(
                    new GetInvoiceByIdQuery { TenantId = tenantId, Id = invoiceId }, ct);
                return invoice?.Number ?? invoiceId.ToString()[..8];
            }
            catch
            {
                return invoiceId.ToString()[..8];
            }
        }
    }
}
