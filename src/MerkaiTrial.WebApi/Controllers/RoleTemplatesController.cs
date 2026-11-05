// =====================================================================
// FILE: MerkaiTrial.WebApi/Controllers/RoleTemplatesController.cs
//
// NEW (117). Starting Roles — the roles every NEW workspace is given
// (RoleTemplates). Platform administration, so super admins only, checked
// on the claim the same way TenantsController does (a permission policy
// would let every workspace admin in: PermissionHandler waves them
// through). A super admin who is currently VIEWING AS someone else is that
// someone else here, and is refused.
//
//   GET  /api/admin/role-templates        the starting roles + the system roles (read only)
//   GET  /api/admin/role-templates/{id}   one starting role
//   POST /api/admin/role-templates        add one
//   PUT  /api/admin/role-templates/{id}   change one (name, description, order, permissions, on/off)
//
// Answers: 400 { error } for anything the person can fix, 404 for a
// starting role that does not exist, 403 { error } for anyone not a super
// admin. Existing workspaces are never changed by any of these.
// =====================================================================

using MerkaiTrial.Application.Commands.RoleTemplates;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace MerkaiTrial.WebApi.Controllers;

[ApiController]
[Authorize]
[Route("api/admin/role-templates")]
public class RoleTemplatesController : ControllerBase
{
    private readonly GetRoleTemplatesHandler _list;
    private readonly GetRoleTemplateHandler _one;
    private readonly CreateRoleTemplateHandler _create;
    private readonly UpdateRoleTemplateHandler _update;
    private readonly ILogger<RoleTemplatesController> _logger;

    public RoleTemplatesController(
        GetRoleTemplatesHandler list, GetRoleTemplateHandler one,
        CreateRoleTemplateHandler create, UpdateRoleTemplateHandler update,
        ILogger<RoleTemplatesController> logger)
    {
        _list = list; _one = one; _create = create; _update = update; _logger = logger;
    }

    private bool IsSuperAdmin =>
        User.HasClaim("IsSuperAdmin", "true") && !User.HasClaim("ViewingAs", "true");

    private string Who =>
        User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
        ?? User.Identity?.Name ?? "super admin";

    private IActionResult? RequireSuperAdmin(string operation)
    {
        if (IsSuperAdmin) return null;
        _logger.LogWarning("Refused platform operation {Operation} for non-super-admin {User}", operation, Who);
        return StatusCode(StatusCodes.Status403Forbidden, new { error = "This is a platform administration endpoint." });
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        if (RequireSuperAdmin("role-templates.list") is { } refused) return refused;
        return Ok(await _list.HandleAsync(ct));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        if (RequireSuperAdmin("role-templates.get") is { } refused) return refused;
        try { return Ok(await _one.HandleAsync(id, ct)); }
        catch (KeyNotFoundException ex) { return NotFound(new { error = ex.Message }); }
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SaveRoleTemplateDto dto, CancellationToken ct)
    {
        if (RequireSuperAdmin("role-templates.create") is { } refused) return refused;
        try
        {
            var created = await _create.HandleAsync(dto, Who, ct);
            _logger.LogInformation("Starting role {Name} added by {User}", created.Name, Who);
            return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] SaveRoleTemplateDto dto, CancellationToken ct)
    {
        if (RequireSuperAdmin("role-templates.update") is { } refused) return refused;
        try
        {
            await _update.HandleAsync(id, dto, Who, ct);
            _logger.LogInformation("Starting role {Id} changed by {User}", id, Who);
            return NoContent();
        }
        catch (KeyNotFoundException ex) { return NotFound(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }
}
