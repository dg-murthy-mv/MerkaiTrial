// =====================================================================
// FILE: MerkaiTrial.WebApi/Controllers/CustomFieldsController.cs
//
// NEW FILE (075 — custom fields, Round A).
//
// TENANT ID COMES FROM THE TOKEN, never the query string — the 068
// ProductCategoriesController rule, for the same reason: these endpoints
// write a workspace's private schema.
//
// ─────────────────────────────────────────────────────────────────────
// AUTHORIZATION — TWO DIFFERENT QUESTIONS
//
//   DEFINING fields (create, edit, switch off, delete, reorder) is
//   configuring the workspace: Settings.Create / Update / Delete, the
//   same module as Product Categories and Tax Rates.
//
//   READING the definitions is needed by anybody who can open the
//   record they belong on — a rep with contacts.read must see the
//   "Renewal date" field on a contact, and typically does NOT hold
//   settings.read. Under Settings.Read the contact pages would quietly
//   get an empty list and show no custom fields at all, with nothing in
//   any log a person would read. That is exactly the bug the 068 note in
//   ProductCategoriesController describes, so the read is gated on
//   "<entity module>.read OR settings.read", decided per request from
//   the entity type asked for. It cannot be a static [Authorize]
//   attribute because the module depends on the query string.
//
//   Writing a VALUE onto a contact is not here at all — it travels with
//   the contact through ContactsController, under contacts.update.
//
//   PermissionHandler bypasses on IsTenantAdmin only, NOT IsSuperAdmin.
//   That is correct here: a platform admin has no business defining a
//   workspace's fields.
// ─────────────────────────────────────────────────────────────────────
// =====================================================================

using MerkaiTrial.Application.Authorization;
using MerkaiTrial.Application.Commands.CustomFields;
using MerkaiTrial.Application.Configuration;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.WebApi.Controllers
{
    [ApiController]
    [Route("api/custom-fields")]
    [Authorize]
    public class CustomFieldsController : ControllerBase
    {
        private readonly GetCustomFieldDefinitionsHandler _get;
        private readonly CreateCustomFieldHandler _create;
        private readonly UpdateCustomFieldHandler _update;
        private readonly DeleteCustomFieldHandler _delete;
        private readonly ReorderCustomFieldsHandler _reorder;
        private readonly IAuthorizationService _authorization;
        private readonly ICurrentUserService _currentUserService;
        private readonly ILogger<CustomFieldsController> _logger;

        public CustomFieldsController(
            GetCustomFieldDefinitionsHandler get,
            CreateCustomFieldHandler create,
            UpdateCustomFieldHandler update,
            DeleteCustomFieldHandler delete,
            ReorderCustomFieldsHandler reorder,
            IAuthorizationService authorization,
            ICurrentUserService currentUserService,
            ILogger<CustomFieldsController> logger)
        {
            _get                = get;
            _create             = create;
            _update             = update;
            _delete             = delete;
            _reorder            = reorder;
            _authorization      = authorization;
            _currentUserService = currentUserService;
            _logger             = logger;
        }

        /// <summary>
        /// The fields defined for one kind of record.
        /// GET api/custom-fields?entityType=Contact&amp;includeInactive=true
        ///
        /// Allowed for anybody who can read that kind of record, or who can
        /// read settings. See the note at the top of this file.
        /// </summary>
        [HttpGet]
        public async Task<ActionResult<List<CustomFieldDefinitionDto>>> GetAll(
            [FromQuery] string? entityType,
            [FromQuery] bool includeInactive = false,
            CancellationToken ct = default)
        {
            var type = CustomFieldEntityTypes.Normalise(entityType);
            if (type is null)
                return BadRequest(new { error = "Custom fields are not available for that kind of record." });

            var recordRead   = await _authorization.AuthorizeAsync(User, $"{CustomFieldEntityTypes.ModuleFor(type)}.{Actions.Read}");
            var settingsRead = recordRead.Succeeded
                ? recordRead
                : await _authorization.AuthorizeAsync(User, Policies.SettingsRead);

            // 403 with a body rather than Forbid(): IApiService turns a 403
            // carrying { error } into a readable exception on the page.
            if (!recordRead.Succeeded && !settingsRead.Succeeded)
                return StatusCode(403, new { error = "You do not have access to these fields." });

            var tenantId = _currentUserService.GetCurrentTenantId();

            try
            {
                return Ok(await _get.Handle(tenantId, type, includeInactive, ct));
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to list custom fields ({Entity}) for {TenantId}", type, tenantId);
                return StatusCode(500, new { error = "Failed to load custom fields" });
            }
        }

        [HttpPost]
        [Authorize(Policy = Policies.SettingsCreate)]
        public async Task<ActionResult<CustomFieldDefinitionDto>> Create(
            [FromBody] SaveCustomFieldDefinitionDto dto,
            CancellationToken ct)
        {
            var tenantId = _currentUserService.GetCurrentTenantId();

            try
            {
                return Ok(await _create.Handle(tenantId, dto, ct));
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to create a custom field for {TenantId}", tenantId);
                return StatusCode(500, new { error = "Failed to add the field" });
            }
        }

        [HttpPut("{id:guid}")]
        [Authorize(Policy = Policies.SettingsUpdate)]
        public async Task<ActionResult<CustomFieldDefinitionDto>> Update(
            Guid id,
            [FromBody] SaveCustomFieldDefinitionDto dto,
            CancellationToken ct)
        {
            var tenantId = _currentUserService.GetCurrentTenantId();

            try
            {
                return Ok(await _update.Handle(tenantId, id, dto, ct));
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
                _logger.LogError(ex, "Failed to update custom field {Id} for {TenantId}", id, tenantId);
                return StatusCode(500, new { error = "Failed to save the field" });
            }
        }

        /// <summary>
        /// Delete a field that no live record uses. A field with values is
        /// refused with a 400 explaining that it should be switched off.
        /// DELETE, unlike product categories: there is nothing to say about
        /// where anything goes, so there is no body.
        /// </summary>
        [HttpDelete("{id:guid}")]
        [Authorize(Policy = Policies.SettingsDelete)]
        public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
        {
            var tenantId = _currentUserService.GetCurrentTenantId();

            try
            {
                await _delete.Handle(tenantId, id, ct);
                return NoContent();
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
                _logger.LogError(ex, "Failed to delete custom field {Id} for {TenantId}", id, tenantId);
                return StatusCode(500, new { error = "Failed to delete the field" });
            }
        }

        [HttpPut("order")]
        [Authorize(Policy = Policies.SettingsUpdate)]
        public async Task<IActionResult> Reorder(
            [FromBody] ReorderCustomFieldsDto dto,
            CancellationToken ct)
        {
            var tenantId = _currentUserService.GetCurrentTenantId();

            try
            {
                var moved = await _reorder.Handle(tenantId, dto, ct);
                return Ok(new { moved });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reorder custom fields for {TenantId}", tenantId);
                return StatusCode(500, new { error = "Failed to save the new order" });
            }
        }
    }
}
