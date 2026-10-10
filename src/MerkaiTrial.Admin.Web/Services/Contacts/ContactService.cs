// =====================================================================
// CONTACT SERVICE
// Location: MerkaiTrial.Admin.Web/Services/Contacts/ContactService.cs
//
// FIXES:
//   Bug 1 — GetLookupAsync now calls /api/contacts/lookup (returns contacts)
//   Bug 5 — tenantId param removed from methods where it was unused
//           (controller resolves tenant server-side via ICurrentUserService)
//   Bug 6 — GetCompaniesLookupAsync added, calls /api/contacts/companies-lookup
//
// 075 — Create and Update let an InvalidOperationException through
//   WITHOUT logging it as an error. That is how the API's 400
//   { error: "Renewal date must be a valid date." } arrives (IApiService
//   converts it), and a person leaving a required field empty is not a
//   system fault worth an ERROR line. The page shows the sentence.
//
// 076 — GetAllAsync takes optional custom field filters, sent as
//   repeated ?cf= values. Optional and last, so every existing caller is
//   unchanged.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Core;
using MerkaiTrial.Application.Configuration;
using MerkaiTrial.Application.DTOs;

namespace MerkaiTrial.Admin.Web.Services.Contacts
{
    public interface IContactService
    {
        Task<PaginatedResult<ContactListItem>> GetAllAsync(
            Guid tenantId,
            int pageNumber = 1,
            int pageSize = 10,
            Guid? companyId = null,
            string? searchTerm = null,
            bool? isPrimary = null,
            IReadOnlyList<CustomFieldFilter>? customFilters = null);   // 076

        Task<ContactStatsDto> GetStatsAsync(Guid tenantId);

        // ✅ BUG 6: returns contacts (for Deal/Lead/Quote dropdowns)
        Task<List<ContactListItem>> GetLookupAsync(Guid tenantId);

        // ✅ BUG 1 + BUG 6: dedicated company lookup for Create/Edit Contact dropdown
        Task<List<CompanyListItem>> GetCompaniesLookupAsync(Guid tenantId);

        Task<ContactDto> GetByIdAsync(Guid tenantId, Guid id);
        Task<List<ContactListItem>> GetByCompanyAsync(Guid tenantId, Guid companyId);
        Task<ContactDto> CreateAsync(CreateContactDto dto);
        Task<ContactDto> UpdateAsync(Guid id, UpdateContactDto dto);
        Task DeleteAsync(Guid tenantId, Guid id);
    }

    public class ContactService : IContactService
    {
        private readonly IApiService _api;
        private readonly ILogger<ContactService> _logger;

        public ContactService(IApiService api, ILogger<ContactService> logger)
        {
            _api    = api;
            _logger = logger;
        }

        // ── GET ALL (PAGINATED) ───────────────────────────────────────────────
        // tenantId kept here as the Web layer passes it in — controller resolves
        // the actual tenant server-side, this param is for call-site clarity only.

        public async Task<PaginatedResult<ContactListItem>> GetAllAsync(
            Guid tenantId,
            int pageNumber = 1,
            int pageSize = 10,
            Guid? companyId = null,
            string? searchTerm = null,
            bool? isPrimary = null,
            IReadOnlyList<CustomFieldFilter>? customFilters = null)
        {
            try
            {
                var queryParams = new List<string>
                {
                    $"pageNumber={pageNumber}",
                    $"pageSize={pageSize}"
                };

                if (companyId.HasValue)
                    queryParams.Add($"companyId={companyId}");
                if (!string.IsNullOrWhiteSpace(searchTerm))
                    queryParams.Add($"searchTerm={Uri.EscapeDataString(searchTerm)}");
                if (isPrimary.HasValue)
                    queryParams.Add($"isPrimary={isPrimary.Value.ToString().ToLowerInvariant()}");

                // 076 — escaped as a whole: the value part is free text.
                foreach (var f in customFilters ?? Array.Empty<CustomFieldFilter>())
                    queryParams.Add($"cf={Uri.EscapeDataString(CustomFieldFilterCodec.Encode(f))}");

                var query = string.Join("&", queryParams);
                return await _api.GetAsync<PaginatedResult<ContactListItem>>($"/api/contacts?{query}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting paginated contacts for tenant {TenantId}", tenantId);
                throw;
            }
        }

        // ── STATS ─────────────────────────────────────────────────────────────

        public async Task<ContactStatsDto> GetStatsAsync(Guid tenantId)
        {
            try
            {
                // ✅ BUG 5: removed unused ?tenantId= query param — controller resolves via ICurrentUserService
                return await _api.GetAsync<ContactStatsDto>("/api/contacts/stats");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting contact stats for tenant {TenantId}", tenantId);
                throw;
            }
        }

        // ── CONTACT LOOKUP (for Deal/Lead/Quote dropdowns) ────────────────────
        // ✅ BUG 1 + BUG 6: /api/contacts/lookup now returns List<ContactListItem>

        public async Task<List<ContactListItem>> GetLookupAsync(Guid tenantId)
        {
            try
            {
                return await _api.GetAsync<List<ContactListItem>>("/api/contacts/lookup");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting contact lookup for tenant {TenantId}", tenantId);
                throw;
            }
        }

        // ── COMPANIES LOOKUP (for Create/Edit Contact company dropdown) ────────
        // ✅ BUG 1 + BUG 6: dedicated endpoint, no longer hijacking /lookup

        public async Task<List<CompanyListItem>> GetCompaniesLookupAsync(Guid tenantId)
        {
            try
            {
                return await _api.GetAsync<List<CompanyListItem>>("/api/contacts/companies-lookup");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting companies lookup for tenant {TenantId}", tenantId);
                throw;
            }
        }

        // ── GET BY ID ─────────────────────────────────────────────────────────

        public async Task<ContactDto> GetByIdAsync(Guid tenantId, Guid id)
        {
            try
            {
                return await _api.GetAsync<ContactDto>($"/api/contacts/{id}");
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("NotFound"))
            {
                throw new KeyNotFoundException($"Contact {id} not found");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting contact {Id}", id);
                throw;
            }
        }

        // ── GET BY COMPANY ────────────────────────────────────────────────────

        public async Task<List<ContactListItem>> GetByCompanyAsync(Guid tenantId, Guid companyId)
        {
            try
            {
                return await _api.GetAsync<List<ContactListItem>>($"/api/contacts/by-company/{companyId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting contacts for company {CompanyId}", companyId);
                throw;
            }
        }

        // ── CREATE ────────────────────────────────────────────────────────────

        public async Task<ContactDto> CreateAsync(CreateContactDto dto)
        {
            try
            {
                return await _api.PostAsync<ContactDto>("/api/contacts", dto);
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("422")
                                              || ex.Message.Contains("UnprocessableEntity")
                                              || ex.Message.Contains("plan_limit_exceeded"))
            {
                // ✅ FIX 2: Surface plan limit as friendly message for the UI
                throw new InvalidOperationException(
                    "You have reached your plan's contact limit. Upgrade your plan to add more contacts.", ex);
            }
            catch (InvalidOperationException)
            {
                throw;   // 075 — a sentence for the person; see header
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating contact");
                throw;
            }
        }

        // ── UPDATE ────────────────────────────────────────────────────────────

        public async Task<ContactDto> UpdateAsync(Guid id, UpdateContactDto dto)
        {
            try
            {
                return await _api.PutAsync<ContactDto>($"/api/contacts/{id}", dto);
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("NotFound"))
            {
                throw new KeyNotFoundException($"Contact {id} not found");
            }
            catch (InvalidOperationException)
            {
                throw;   // 075 — a sentence for the person; see header
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating contact {Id}", id);
                throw;
            }
        }

        // ── DELETE ────────────────────────────────────────────────────────────

        public async Task DeleteAsync(Guid tenantId, Guid id)
        {
            try
            {
                await _api.DeleteAsync($"/api/contacts/{id}");
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("NotFound"))
            {
                throw new KeyNotFoundException($"Contact {id} not found");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting contact {Id}", id);
                throw;
            }
        }
    }
}
