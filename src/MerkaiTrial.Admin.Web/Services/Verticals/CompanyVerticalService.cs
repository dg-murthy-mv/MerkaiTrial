// =====================================================================
// COMPANY VERTICAL SERVICE - UPDATED with Stats
// Location: MerkaiTrial.Admin.Web/Services/Verticals/CompanyVerticalService.cs
// REPLACE ENTIRE FILE
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Core;
using MerkaiTrial.Application.DTOs;

namespace MerkaiTrial.Admin.Web.Services.Verticals
{
    public interface ICompanyVerticalService
    {
        Task<PaginatedResult<CompanyVerticalListItem>> GetAllAsync(
            Guid? tenantId = null,
            int pageNumber = 1,
            int pageSize = 10,
            string? searchTerm = null,
            bool showSystemOnly = false,
            bool showCustomOnly = false,
            bool showAllVerticals = false);

        Task<List<CompanyVerticalListItem>> GetAvailableForTenantAsync(Guid tenantId);
        Task<CompanyVerticalDto> GetByIdAsync(Guid id);
        Task<VerticalStatsDto> GetStatsAsync();  // ✅ ADDED
        Task<CompanyVerticalDto> CreateAsync(CreateCompanyVerticalDto dto);
        Task UpdateAsync(Guid id, UpdateCompanyVerticalDto dto);
        Task DeleteAsync(Guid id);
    }

    public class CompanyVerticalService : ICompanyVerticalService
    {
        private readonly IApiService _api;
        private readonly ILogger<CompanyVerticalService> _logger;

        public CompanyVerticalService(IApiService api, ILogger<CompanyVerticalService> logger)
        {
            _api = api;
            _logger = logger;
        }

        public async Task<PaginatedResult<CompanyVerticalListItem>> GetAllAsync(
            Guid? tenantId = null,
            int pageNumber = 1,
            int pageSize = 10,
            string? searchTerm = null,
            bool showSystemOnly = false,
            bool showCustomOnly = false,
            bool showAllVerticals = false)
        {
            try
            {
                var queryParams = new List<string>
                {
                    $"pageNumber={pageNumber}",
                    $"pageSize={pageSize}"
                };

                if (tenantId.HasValue && tenantId.Value != Guid.Empty)
                {
                    queryParams.Add($"tenantId={tenantId.Value}");
                }

                if (!string.IsNullOrWhiteSpace(searchTerm))
                {
                    queryParams.Add($"searchTerm={Uri.EscapeDataString(searchTerm)}");
                }

                if (showSystemOnly)
                {
                    queryParams.Add("showSystemOnly=true");
                }

                if (showCustomOnly)
                {
                    queryParams.Add("showCustomOnly=true");
                }

                if (showAllVerticals)
                {
                    queryParams.Add("showAllVerticals=true");
                }

                var query = string.Join("&", queryParams);
                return await _api.GetAsync<PaginatedResult<CompanyVerticalListItem>>($"/api/company-verticals?{query}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting paginated verticals");
                throw;
            }
        }

        public async Task<List<CompanyVerticalListItem>> GetAvailableForTenantAsync(Guid tenantId)
        {
            try
            {
                var result = await _api.GetAsync<PaginatedResult<CompanyVerticalListItem>>(
                    $"/api/company-verticals/available?tenantId={tenantId}&pageSize=1000");
                return result.Items;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting available verticals for tenant {TenantId}", tenantId);
                throw;
            }
        }

        public async Task<CompanyVerticalDto> GetByIdAsync(Guid id)
        {
            try
            {
                return await _api.GetAsync<CompanyVerticalDto>($"/api/company-verticals/{id}");
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("404"))
            {
                _logger.LogWarning("Company vertical {Id} not found", id);
                throw new KeyNotFoundException($"Vertical {id} not found");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting vertical {Id}", id);
                throw;
            }
        }

        // ==================== GET STATISTICS (NEW!) ====================
        public async Task<VerticalStatsDto> GetStatsAsync()
        {
            try
            {
                return await _api.GetAsync<VerticalStatsDto>("/api/company-verticals/stats");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting vertical statistics");
                throw;
            }
        }

        public async Task<CompanyVerticalDto> CreateAsync(CreateCompanyVerticalDto dto)
        {
            try
            {
                return await _api.PostAsync<CompanyVerticalDto>("/api/company-verticals", dto);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating vertical {Name}", dto.Name);
                throw;
            }
        }

        public async Task UpdateAsync(Guid id, UpdateCompanyVerticalDto dto)
        {
            try
            {
                await _api.PutVoidAsync($"/api/company-verticals/{id}", dto);
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("404"))
            {
                _logger.LogWarning("Company vertical {Id} not found for update", id);
                throw new KeyNotFoundException($"Vertical {id} not found");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating vertical {Id}", id);
                throw;
            }
        }

        public async Task DeleteAsync(Guid id)
        {
            try
            {
                await _api.DeleteAsync($"/api/company-verticals/{id}");
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("404"))
            {
                _logger.LogWarning("Company vertical {Id} not found for deletion", id);
                throw new KeyNotFoundException($"Vertical {id} not found");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting vertical {Id}", id);
                throw;
            }
        }
    }
}
