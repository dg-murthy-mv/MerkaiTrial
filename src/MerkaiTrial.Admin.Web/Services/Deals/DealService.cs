// =====================================================================
// DEAL SERVICE - UPDATED
// Location: MerkaiTrial.Admin.Web/Services/Deals/DealService.cs
//
// CONSISTENCY FIXES vs LeadService:
//   1.  Added try-catch + logging to ALL methods (was missing everywhere)
//   2.  Added PlanLimitExceededException catch in CreateAsync
//   3.  Renamed _apiService → _api (consistent with LeadService)
//   4.  Fixed GetAttachmentsAsync — was silently swallowing exceptions
//   5.  Fixed CompleteReminderAsync URL bug — was missing {dealId} in route
//       Interface updated: added dealId param (call site must pass dealId)
//   6.  Fixed DeleteAttachmentAsync — replaced Guid.Empty hack with clean
//       direct endpoint (api/deals/attachments/{id}) which exists in controller
//   7.  Added section headers matching LeadService structure
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Core;
using MerkaiTrial.Application.Commands.Deals;
using MerkaiTrial.Application.DTOs;

namespace MerkaiTrial.Admin.Web.Services.Deals
{
    public interface IDealService
    {
        // Core CRUD
        Task<GetDealsResponse> GetAllAsync(Guid tenantId, string? stage = null,
            string? search = null, string? ownerUserId = null, int page = 1, int pageSize = 20);
        Task<DealDto> GetByIdAsync(Guid tenantId, Guid id);
        Task<DealDetailDto> GetDetailAsync(Guid tenantId, Guid id);
        Task<DealDto> CreateAsync(CreateDealDto dto);
        Task UpdateAsync(Guid tenantId, Guid id, UpdateDealDto dto);
        Task DeleteAsync(Guid tenantId, Guid id);

        // Stage
        Task UpdateStageAsync(Guid tenantId, Guid dealId, string stage);
        Task<List<DealStageHistoryDto>> GetStageHistoryAsync(Guid tenantId, Guid dealId);
        Task<List<DealSourceDto>> GetSourcesAsync(Guid tenantId);
        Task TransitionStageAsync(string tenantId, Guid dealId, string toStage, int probability);

        // Notes
        Task<List<DealNoteDto>> GetNotesAsync(Guid tenantId, Guid dealId);
        Task AddNoteAsync(CreateDealNoteDto dto);

        // Activities
        Task<List<DealActivityDto>> GetActivitiesAsync(Guid tenantId, Guid dealId);
        Task AddActivityAsync(CreateDealActivityDto dto);

        // Reminders
        Task<List<DealReminderDto>> GetRemindersAsync(Guid tenantId, Guid dealId);
        Task AddReminderAsync(CreateDealReminderDto dto);
        // ✅ FIX 5: Added dealId — controller route requires it as {id:guid}
        Task CompleteReminderAsync(Guid tenantId, Guid dealId, Guid reminderId);

        // Attachments
        Task<List<AttachmentDto>> GetAttachmentsAsync(Guid tenantId, Guid dealId);
        Task<AttachmentDto> UploadAttachmentAsync(Guid tenantId, Guid dealId, IFormFile file, string uploadedBy);
        Task DeleteAttachmentAsync(Guid tenantId, Guid attachmentId);

        // Contact relationship
        Task<List<ContactDealItem>> GetByContactAsync(string tenantId, Guid contactId);
    }

    public class DealService : IDealService
    {
        private readonly IApiService _api;            // ✅ FIX 3: renamed from _apiService
        private readonly ILogger<DealService> _logger;

        public DealService(IApiService apiService, ILogger<DealService> logger)
        {
            _api = apiService;
            _logger = logger;
        }

        // ==================== CORE CRUD ====================

        public async Task<GetDealsResponse> GetAllAsync(Guid tenantId, string? stage = null,
            string? search = null, string? ownerUserId = null, int page = 1, int pageSize = 20)
        {
            try
            {
                var query = $"api/deals?tenantId={tenantId}&page={page}&pageSize={pageSize}";
                if (!string.IsNullOrEmpty(stage)) query += $"&stage={stage}";
                if (!string.IsNullOrEmpty(search)) query += $"&search={Uri.EscapeDataString(search)}";
                if (!string.IsNullOrEmpty(ownerUserId)) query += $"&ownerUserId={ownerUserId}";

                return await _api.GetAsync<GetDealsResponse>(query);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting deals for tenant {TenantId}", tenantId);
                throw;
            }
        }

        public async Task<DealDto> GetByIdAsync(Guid tenantId, Guid id)
        {
            try
            {
                return await _api.GetAsync<DealDto>($"api/deals/{id}?tenantId={tenantId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting deal {DealId}", id);
                throw;
            }
        }

        public async Task<DealDetailDto> GetDetailAsync(Guid tenantId, Guid id)
        {
            try
            {
                return await _api.GetAsync<DealDetailDto>($"api/deals/{id}?tenantId={tenantId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting deal detail {DealId}", id);
                throw;
            }
        }

        public async Task<DealDto> CreateAsync(CreateDealDto dto)
        {
            try
            {
                return await _api.PostAsync<DealDto>("api/deals", dto);
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("422")
                                               || ex.Message.Contains("UnprocessableEntity")
                                               || ex.Message.Contains("plan_limit_exceeded"))
            {
                // ✅ FIX 2: Surface plan limit as friendly message for the UI
                throw new InvalidOperationException(
                    "You have reached your plan's deal limit. Upgrade your plan to add more deals.", ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating deal for tenant {TenantId}", dto.TenantId);
                throw;
            }
        }

        public async Task UpdateAsync(Guid tenantId, Guid id, UpdateDealDto dto)
        {
            try
            {
                await _api.PutVoidAsync($"api/deals/{id}?tenantId={tenantId}", dto);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating deal {DealId}", id);
                throw;
            }
        }

        public async Task DeleteAsync(Guid tenantId, Guid id)
        {
            try
            {
                await _api.DeleteAsync($"api/deals/{id}?tenantId={tenantId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting deal {DealId}", id);
                throw;
            }
        }

        // ==================== STAGE ====================

        public async Task UpdateStageAsync(Guid tenantId, Guid dealId, string stage)
        {
            try
            {
                await _api.PutVoidAsync(
                    $"api/deals/{dealId}/stage?tenantId={tenantId}",
                    new { Stage = stage });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating stage for deal {DealId}", dealId);
                throw;
            }
        }

        public async Task<List<DealStageHistoryDto>> GetStageHistoryAsync(Guid tenantId, Guid dealId)
        {
            try
            {
                return await _api.GetAsync<List<DealStageHistoryDto>>(
                    $"api/deals/{dealId}/stage-history?tenantId={tenantId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting stage history for deal {DealId}", dealId);
                throw;
            }
        }

        public async Task<List<DealSourceDto>> GetSourcesAsync(Guid tenantId)
        {
            try
            {
                return await _api.GetAsync<List<DealSourceDto>>(
                    $"api/deals/sources?tenantId={tenantId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting deal sources");
                throw;
            }
        }

        public async Task TransitionStageAsync(string tenantId, Guid dealId, string toStage, int probability)
        {
            try
            {
                await _api.PatchVoidAsync(
                    $"api/deals/{dealId}/stage?tenantId={tenantId}",
                    new { Stage = toStage, Probability = probability });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error transitioning deal {DealId} to stage {Stage}",
                    dealId, toStage);
                throw;
            }
        }

        // ==================== NOTES ====================

        public async Task<List<DealNoteDto>> GetNotesAsync(Guid tenantId, Guid dealId)
        {
            try
            {
                return await _api.GetAsync<List<DealNoteDto>>(
                    $"api/deals/{dealId}/notes?tenantId={tenantId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting notes for deal {DealId}", dealId);
                throw;
            }
        }

        public async Task AddNoteAsync(CreateDealNoteDto dto)
        {
            try
            {
                await _api.PostVoidAsync($"api/deals/{dto.DealId}/notes", dto);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding note to deal {DealId}", dto.DealId);
                throw;
            }
        }

        // ==================== ACTIVITIES ====================

        public async Task<List<DealActivityDto>> GetActivitiesAsync(Guid tenantId, Guid dealId)
        {
            try
            {
                return await _api.GetAsync<List<DealActivityDto>>(
                    $"api/deals/{dealId}/activities?tenantId={tenantId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting activities for deal {DealId}", dealId);
                throw;
            }
        }

        public async Task AddActivityAsync(CreateDealActivityDto dto)
        {
            try
            {
                await _api.PostVoidAsync($"api/deals/{dto.DealId}/activities", dto);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding activity to deal {DealId}", dto.DealId);
                throw;
            }
        }

        // ==================== REMINDERS ====================

        public async Task<List<DealReminderDto>> GetRemindersAsync(Guid tenantId, Guid dealId)
        {
            try
            {
                return await _api.GetAsync<List<DealReminderDto>>(
                    $"api/deals/{dealId}/reminders?tenantId={tenantId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting reminders for deal {DealId}", dealId);
                throw;
            }
        }

        public async Task AddReminderAsync(CreateDealReminderDto dto)
        {
            try
            {
                await _api.PostVoidAsync($"api/deals/{dto.DealId}/reminders", dto);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding reminder to deal {DealId}", dto.DealId);
                throw;
            }
        }

        // ✅ FIX 5: Added dealId param — controller route is {id:guid}/reminders/{reminderId:guid}/complete
        // Without dealId the URL was malformed and would return 404 every time.
        // Update all call sites to pass dealId (typically available on the Deal Detail page).
        public async Task CompleteReminderAsync(Guid tenantId, Guid dealId, Guid reminderId)
        {
            try
            {
                await _api.PostVoidAsync(
                    $"api/deals/{dealId}/reminders/{reminderId}/complete?tenantId={tenantId}",
                    null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error completing reminder {ReminderId} for deal {DealId}",
                    reminderId, dealId);
                throw;
            }
        }

        // ==================== ATTACHMENTS ====================

        public async Task<List<AttachmentDto>> GetAttachmentsAsync(Guid tenantId, Guid dealId)
        {
            try
            {
                // ✅ FIX 4: Was silently returning empty list on error — now throws so caller knows
                return await _api.GetAsync<List<AttachmentDto>>(
                    $"api/deals/{dealId}/attachments?tenantId={tenantId}")
                    ?? new List<AttachmentDto>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting attachments for deal {DealId}", dealId);
                throw;
            }
        }

        public async Task<AttachmentDto> UploadAttachmentAsync(
            Guid tenantId, Guid dealId, IFormFile file, string uploadedBy)
        {
            try
            {
                var url = $"api/deals/{dealId}/attachments?tenantId={tenantId}";

                using var content = new MultipartFormDataContent();
                await using var stream = file.OpenReadStream();
                var fileContent = new StreamContent(stream);
                fileContent.Headers.ContentType =
                    new System.Net.Http.Headers.MediaTypeHeaderValue(file.ContentType);
                content.Add(fileContent, "file", file.FileName);

                return await _api.PostMultipartAsync<AttachmentDto>(url, content);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error uploading attachment to deal {DealId}", dealId);
                throw;
            }
        }

        public async Task DeleteAttachmentAsync(Guid tenantId, Guid attachmentId)
        {
            try
            {
                // ✅ FIX 6: Use direct endpoint — no fake Guid.Empty needed
                // Controller has: DELETE api/deals/attachments/{attachmentId}?tenantId=...
                await _api.DeleteAsync(
                    $"api/deals/attachments/{attachmentId}?tenantId={tenantId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting attachment {AttachmentId}", attachmentId);
                throw;
            }
        }

        // ==================== CONTACT RELATIONSHIP ====================

        public async Task<List<ContactDealItem>> GetByContactAsync(string tenantId, Guid contactId)
        {
            try
            {
                return await _api.GetAsync<List<ContactDealItem>>(
                    $"api/deals/by-contact/{contactId}?tenantId={tenantId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting deals for contact {ContactId}", contactId);
                throw;
            }
        }
    }
}
