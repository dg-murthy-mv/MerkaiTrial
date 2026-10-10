// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Services/CustomFields/CustomFieldService.cs
//
// NEW FILE (075 — custom fields, Round A).
//
// Thin wrapper over /api/custom-fields, in the shape of
// ProductCategoryService. No tenantId anywhere: the controller reads the
// workspace from the signed token.
//
// READS THROW. That is a deliberate difference from
// ProductCategoryService.GetAllAsync, which degrades to an empty list:
//
//   On the contact Edit page, "no custom fields" and "custom fields
//   could not be loaded" must not look the same. If they did, the form
//   would render without the fields, the person would save, and — because
//   an empty map means "nothing to say" for absent keys — nothing would
//   be lost, but they would also never learn that the "Renewal date" they
//   came to change is not on the page. Each page catches the exception
//   and shows its own sentence.
//
// Writes surface the API's own sentence: a 400 { error } becomes an
// InvalidOperationException carrying it ("There is already a field
// called \"Renewal date\".").
//
// Registered in AdminWebServiceRegistration.AddAdminWebModuleServices.
// Without that line the app starts and then throws "Unable to resolve
// service for type 'ICustomFieldService'" the first time a contact opens.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Core;
using MerkaiTrial.Application.DTOs;

namespace MerkaiTrial.Admin.Web.Services.CustomFields
{
    public interface ICustomFieldService
    {
        /// <summary>
        /// The fields for one entity type ("Contact"), in display order.
        /// includeInactive: true for the Settings page and record detail
        /// pages; false for Create/Edit forms.
        /// </summary>
        Task<List<CustomFieldDefinitionDto>> GetDefinitionsAsync(string entityType, bool includeInactive);

        Task<CustomFieldDefinitionDto> CreateAsync(SaveCustomFieldDefinitionDto dto);
        Task<CustomFieldDefinitionDto> UpdateAsync(Guid id, SaveCustomFieldDefinitionDto dto);
        Task DeleteAsync(Guid id);
        Task<int> ReorderAsync(string entityType, List<Guid> orderedIds);
    }

    public class CustomFieldService : ICustomFieldService
    {
        private readonly IApiService _api;
        private readonly ILogger<CustomFieldService> _logger;

        public CustomFieldService(IApiService api, ILogger<CustomFieldService> logger)
        {
            _api    = api;
            _logger = logger;
        }

        public async Task<List<CustomFieldDefinitionDto>> GetDefinitionsAsync(string entityType, bool includeInactive)
        {
            try
            {
                var url = $"api/custom-fields?entityType={Uri.EscapeDataString(entityType)}" +
                          $"&includeInactive={includeInactive.ToString().ToLowerInvariant()}";

                return await _api.GetAsync<List<CustomFieldDefinitionDto>>(url)
                       ?? new List<CustomFieldDefinitionDto>();
            }
            catch (Exception ex)
            {
                // Logged here, re-thrown for the page to explain. See header.
                _logger.LogError(ex, "Failed to load custom fields for {EntityType}", entityType);
                throw;
            }
        }

        public async Task<CustomFieldDefinitionDto> CreateAsync(SaveCustomFieldDefinitionDto dto)
            => await _api.PostAsync<CustomFieldDefinitionDto>("api/custom-fields", dto)
               ?? throw new InvalidOperationException("The field could not be added. Please try again.");

        public async Task<CustomFieldDefinitionDto> UpdateAsync(Guid id, SaveCustomFieldDefinitionDto dto)
            => await _api.PutAsync<CustomFieldDefinitionDto>($"api/custom-fields/{id}", dto)
               ?? throw new InvalidOperationException("The field could not be saved. Please try again.");

        public async Task DeleteAsync(Guid id)
            => await _api.DeleteAsync($"api/custom-fields/{id}");

        public async Task<int> ReorderAsync(string entityType, List<Guid> orderedIds)
        {
            var result = await _api.PutAsync<ReorderResponse>(
                "api/custom-fields/order",
                new ReorderCustomFieldsDto { EntityType = entityType, OrderedIds = orderedIds ?? new List<Guid>() });

            return result?.Moved ?? 0;
        }

        private sealed class ReorderResponse
        {
            public int Moved { get; set; }
        }
    }
}
