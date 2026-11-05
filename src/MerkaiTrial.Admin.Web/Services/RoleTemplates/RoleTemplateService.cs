// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Services/RoleTemplates/RoleTemplateService.cs
//
// NEW (117). Thin wrapper over /api/admin/role-templates — the Starting
// Roles every new workspace is given. Super admin pages only.
//
// Errors come back the way IApiService reports them: a 400/403 with
// { error } as InvalidOperationException carrying the API's sentence, a
// 404 as KeyNotFoundException.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Core;
using MerkaiTrial.Application.Commands.RoleTemplates;

namespace MerkaiTrial.Admin.Web.Services.RoleTemplates
{
    public interface IRoleTemplateService
    {
        Task<RoleTemplatesOverviewDto> GetAllAsync();
        Task<RoleTemplateDto> GetAsync(Guid id);
        Task<RoleTemplateDto> CreateAsync(SaveRoleTemplateDto dto);
        Task UpdateAsync(Guid id, SaveRoleTemplateDto dto);
    }

    public class RoleTemplateService : IRoleTemplateService
    {
        private readonly IApiService _api;

        public RoleTemplateService(IApiService api) => _api = api;

        public async Task<RoleTemplatesOverviewDto> GetAllAsync()
            => await _api.GetAsync<RoleTemplatesOverviewDto>("api/admin/role-templates") ?? new RoleTemplatesOverviewDto();

        public async Task<RoleTemplateDto> GetAsync(Guid id)
            => await _api.GetAsync<RoleTemplateDto>($"api/admin/role-templates/{id}")
               ?? throw new KeyNotFoundException("That starting role does not exist.");

        public Task<RoleTemplateDto> CreateAsync(SaveRoleTemplateDto dto)
            => _api.PostAsync<RoleTemplateDto>("api/admin/role-templates", dto);

        public Task UpdateAsync(Guid id, SaveRoleTemplateDto dto)
            => _api.PutVoidAsync($"api/admin/role-templates/{id}", dto);
    }
}
