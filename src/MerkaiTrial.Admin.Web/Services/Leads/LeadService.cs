// =====================================================================
// LEAD SERVICE - Updated with Channels/Sources
// Location: MerkaiTrial.Admin.Web/Services/Leads/LeadService.cs
//
// 047 CHANGE: GetStatsAsync can now be given a date range, so the
// Dashboard's lead card honours the fiscal period like the quote and
// invoice cards have since 043a.
//
// Two optional TRAILING parameters, which is the same shape QuoteService
// and InvoiceService took in 043a. Every existing GetStatsAsync(tenantId)
// call still compiles and still means all time.
//
// WHICH RANGE TO PASS — this one matters:
//
//   LeadsController binds these to CreatedAtUtc, which is a TIMESTAMP.
//   So the caller passes FiscalPeriodOption.Timestamps, NOT .Dates.
//
//   The two are not interchangeable. Timestamps is shifted by the
//   tenant's timezone, because "1 April in Bangkok" starts at 17:00 UTC
//   on 31 March. Dates is deliberately NOT shifted, because a column
//   holding a date rather than a moment has no timezone to shift by.
//   Pass Dates here and every lead created in the last seven hours of a
//   period would land in the next one.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Core;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Domain.Enums;

namespace MerkaiTrial.Admin.Web.Services.Leads
{
    public interface ILeadService
    {
        // Core CRUD
        Task<LeadDto> CreateAsync(CreateLeadDto dto);
        Task<LeadDetailDto> GetByIdAsync(Guid tenantId, Guid leadId);
        Task<LeadDetailDto> UpdateAsync(UpdateLeadDto dto);
        Task UpdateStatusAsync(Guid tenantId, Guid leadId, string status);
        Task DeleteAsync(Guid tenantId, Guid leadId);
        Task<List<SalesTeamMemberDto>> GetSalesTeamAsync(Guid tenantId);
        Task<List<CountryDropdownDto>> GetCountriesAsync();
        Task<ConvertLeadToDealResultDto> ConvertToDealAsync(ConvertLeadToDealDto dto);
        Task<List<CurrencyDropdownDto>> GetCurrenciesAsync();
        // Listing & Stats
        Task<PaginatedResult<LeadListItem>> GetPaginatedAsync(
            Guid tenantId,
            int pageNumber,
            int pageSize,
            string? searchTerm = null,
            string? status = null,
            string? assignedTo = null);
        /// <summary>
        /// 047: the two dates are optional and TRAILING, so every existing
        /// GetStatsAsync(tenantId) call still compiles and still means all
        /// time. Half-open — toExclusiveUtc is the first instant NOT
        /// included. Bound to CreatedAtUtc server side, so pass
        /// IFiscalYearService's TIMESTAMP range, not the date-only one.
        /// </summary>
        Task<LeadStatsDto> GetStatsAsync(
            Guid tenantId, DateTime? fromUtc = null, DateTime? toExclusiveUtc = null);

        // Lookups (NEW - for dynamic dropdowns)
        Task<List<LeadChannelDto>> GetChannelsAsync(Guid tenantId);
        Task<List<LeadSourceDto>> GetSourcesAsync(Guid tenantId);

        // Conversion
        Task<ConvertLeadResultDto> ConvertAsync(ConvertLeadDto dto);

        // Notes
        Task<LeadNoteDto> CreateNoteAsync(CreateLeadNoteDto dto);
        Task<List<LeadNoteDto>> GetNotesAsync(Guid tenantId, Guid leadId);
        Task DeleteNoteAsync(Guid tenantId, Guid leadId, Guid noteId);

        // Activities
        Task<LeadActivityDto> CreateActivityAsync(CreateLeadActivityDto dto);
        Task<List<LeadActivityDto>> GetActivitiesAsync(Guid tenantId, Guid leadId);

        // Reminders
        Task<LeadReminderDto> CreateReminderAsync(CreateLeadReminderDto dto);
        Task<List<LeadReminderDto>> GetRemindersAsync(Guid tenantId, Guid leadId);
        Task CompleteReminderAsync(Guid tenantId, Guid reminderId);

        // Assignment & Timeline
        Task AssignAsync(AssignLeadDto dto);
        Task<List<TimelineItemDto>> GetTimelineAsync(Guid tenantId, Guid leadId);

        // Export/Import
        Task<byte[]> ExportAsync(Guid tenantId, string? search, string? status, string? assignedTo);
       // Task<ImportLeadsResult> ImportAsync(Guid tenantId, IFormFile file, string? importedBy);

        // Attachments
        Task<AttachmentDto> UploadAttachmentAsync(Guid tenantId, Guid leadId, IFormFile file, string? uploadedBy);
        Task<List<AttachmentDto>> GetAttachmentsAsync(Guid tenantId, Guid leadId);
        Task DeleteAttachmentAsync(Guid tenantId, Guid attachmentId);
    }

    public class LeadService : ILeadService
    {
        private readonly IApiService _api;
        private readonly ILogger<LeadService> _logger;

        public LeadService(IApiService api, ILogger<LeadService> logger)
        {
            _api = api;
            _logger = logger;
        }

        // ==================== CORE CRUD ====================

        public async Task<LeadDto> CreateAsync(CreateLeadDto dto)
        {
            try
            {
                return await _api.PostAsync<LeadDto>("/api/leads", dto);
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("422")
                                       || ex.Message.Contains("UnprocessableEntity")
                                       || ex.Message.Contains("plan_limit_exceeded"))
            {
                // Surface plan limit as a friendly message for the UI
                throw new InvalidOperationException(
                    "You have reached your plan's lead limit. Upgrade your plan to add more leads.", ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating lead");
                throw;
            }
        }

        public async Task<List<CountryDropdownDto>> GetCountriesAsync()
        {
            try
            {
                return await _api.GetAsync<List<CountryDropdownDto>>("/api/leads/countries");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting countries");
                throw;
            }
        }
        public async Task<ConvertLeadToDealResultDto> ConvertToDealAsync(ConvertLeadToDealDto dto)
        {
            try
            {
                _logger.LogInformation("Converting lead {LeadId} to deal", dto.LeadId);

                var result = await _api.PostAsync<ConvertLeadToDealResultDto>(
                    $"/api/leads/{dto.LeadId}/convert-to-deal",
                    dto);

                _logger.LogInformation("Lead converted successfully. DealId: {DealId}", result.DealId);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to convert lead {LeadId} to deal", dto.LeadId);
                throw;
            }
        }
        public async Task<List<CurrencyDropdownDto>> GetCurrenciesAsync()
        {
            try
            {
                return await _api.GetAsync<List<CurrencyDropdownDto>>("/api/leads/currencies");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting currencies");
                throw;
            }
        }
        public async Task<List<SalesTeamMemberDto>> GetSalesTeamAsync(Guid tenantId)
        {
            try
            {
                return await _api.GetAsync<List<SalesTeamMemberDto>>("/api/leads/sales-team");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting sales team");
                throw;
            }
        }
        public async Task<LeadDetailDto> GetByIdAsync(Guid tenantId, Guid leadId)
        {
            try
            {
                return await _api.GetAsync<LeadDetailDto>($"/api/leads/{leadId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting lead {LeadId}", leadId);
                throw;
            }
        }

        public async Task<LeadDetailDto> UpdateAsync(UpdateLeadDto dto)
        {
            try
            {
                return await _api.PutAsync<LeadDetailDto>($"/api/leads/{dto.LeadId}", dto);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating lead {LeadId}", dto.LeadId);
                throw;
            }
        }

        public async Task UpdateStatusAsync(Guid tenantId, Guid leadId, string status)
        {
            try
            {
                await _api.PutAsync<object>(
                     $"/api/leads/{leadId}/status?status={Uri.EscapeDataString(status)}", null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating lead status {LeadId}", leadId);
                throw;
            }
        }

        public async Task DeleteAsync(Guid tenantId, Guid leadId)
        {
            try
            {
                await _api.DeleteAsync($"/api/leads/{leadId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting lead {LeadId}", leadId);
                throw;
            }
        }

        // ==================== LISTING & STATS ====================

        public async Task<PaginatedResult<LeadListItem>> GetPaginatedAsync(
            Guid tenantId,
            int pageNumber,
            int pageSize,
            string? searchTerm = null,
            string? status = null,
            string? assignedTo = null)
        {
            try
            {
                var queryParams = new List<string>
                {
                    $"pageNumber={pageNumber}",
                    $"pageSize={pageSize}"
                };

                if (!string.IsNullOrWhiteSpace(searchTerm))
                    queryParams.Add($"searchTerm={Uri.EscapeDataString(searchTerm)}");
                if (!string.IsNullOrWhiteSpace(status))
                    queryParams.Add($"status={status}");
                if (!string.IsNullOrWhiteSpace(assignedTo))
                    queryParams.Add($"assignedTo={Uri.EscapeDataString(assignedTo)}");

                var query = string.Join("&", queryParams);
                return await _api.GetAsync<PaginatedResult<LeadListItem>>($"/api/leads?{query}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting paginated leads");
                throw;
            }
        }

        public async Task<LeadStatsDto> GetStatsAsync(
            Guid tenantId, DateTime? fromUtc = null, DateTime? toExclusiveUtc = null)
        {
            try
            {
                var url = "/api/leads/stats";

                // Built as a list so the first parameter gets "?" and the
                // rest get "&" without a counter to get wrong.
                var q = new List<string>();

                if (fromUtc.HasValue)        q.Add($"from={Utc(fromUtc.Value)}");
                if (toExclusiveUtc.HasValue) q.Add($"toExclusive={Utc(toExclusiveUtc.Value)}");

                if (q.Count > 0) url += "?" + string.Join("&", q);

                return await _api.GetAsync<LeadStatsDto>(url);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting lead stats");
                throw;
            }
        }

        /// <summary>
        /// 047. The same formatter QuoteService and InvoiceService use, for
        /// the same reason.
        ///
        /// ToString("o") emits seven fractional digits, which some model
        /// binders round-trip badly, and an UNSPECIFIED DateTime formatted
        /// without the Z is read by the API as ITS local time — a seven-hour
        /// error here in Asia/Bangkok, quietly, in the direction of showing
        /// the wrong month's leads.
        ///
        /// So: force the kind, then state it in the string.
        /// </summary>
        private static string Utc(DateTime value)
        {
            var utc = value.Kind switch
            {
                DateTimeKind.Utc   => value,
                DateTimeKind.Local => value.ToUniversalTime(),
                _                  => DateTime.SpecifyKind(value, DateTimeKind.Utc)
            };

            return Uri.EscapeDataString(utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"));
        }

        // ==================== LOOKUPS (NEW) ====================

        public async Task<List<LeadChannelDto>> GetChannelsAsync(Guid tenantId)
        {
            try
            {
                return await _api.GetAsync<List<LeadChannelDto>>("/api/leads/channels");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting lead channels");
                throw;
            }
        }

        public async Task<List<LeadSourceDto>> GetSourcesAsync(Guid tenantId)
        {
            try
            {
                return await _api.GetAsync<List<LeadSourceDto>>("/api/leads/sources");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting lead sources");
                throw;
            }
        }

        // ==================== CONVERSION ====================

        public async Task<ConvertLeadResultDto> ConvertAsync(ConvertLeadDto dto)
        {
            try
            {
                return await _api.PostAsync<ConvertLeadResultDto>($"/api/leads/{dto.LeadId}/convert", dto);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error converting lead {LeadId}", dto.LeadId);
                throw;
            }
        }

        public async Task<AttachmentDto> UploadAttachmentAsync(
        Guid tenantId, Guid leadId, IFormFile file, string? uploadedBy)
        {
            using var content = new MultipartFormDataContent();
            using var stream = file.OpenReadStream();
            var fileContent = new StreamContent(stream);
            fileContent.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue(file.ContentType);
            content.Add(fileContent, "file", file.FileName);

            return await _api.PostMultipartAsync<AttachmentDto>(
                $"api/leads/{leadId}/attachments?tenantId={tenantId}", content);
        }

        public async Task<List<AttachmentDto>> GetAttachmentsAsync(Guid tenantId, Guid leadId)
            => await _api.GetAsync<List<AttachmentDto>>(
                $"api/leads/{leadId}/attachments?tenantId={tenantId}");

        public async Task DeleteAttachmentAsync(Guid tenantId, Guid attachmentId)
            => await _api.DeleteAsync(
                $"api/leads/attachments/{attachmentId}?tenantId={tenantId}");

        // ==================== NOTES ====================

        public async Task<LeadNoteDto> CreateNoteAsync(CreateLeadNoteDto dto)
        {
            try
            {
                return await _api.PostAsync<LeadNoteDto>($"/api/leads/{dto.LeadId}/notes", dto);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating note for lead {LeadId}", dto.LeadId);
                throw;
            }
        }

        public async Task<List<LeadNoteDto>> GetNotesAsync(Guid tenantId, Guid leadId)
        {
            try
            {
                // ✅ FIXED: Add tenantId query parameter
                return await _api.GetAsync<List<LeadNoteDto>>($"/api/leads/{leadId}/notes?tenantId={tenantId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting notes for lead {LeadId}", leadId);
                throw;
            }
        }

        public async Task DeleteNoteAsync(Guid tenantId, Guid leadId, Guid noteId)
        {
            try
            {
                // ✅ FIXED: Add tenantId query parameter
                await _api.DeleteAsync($"/api/leads/notes/{noteId}?tenantId={tenantId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting note {NoteId}", noteId);
                throw;
            }
        }

        // ==================== ACTIVITIES ====================

        public async Task<LeadActivityDto> CreateActivityAsync(CreateLeadActivityDto dto)
        {
            try
            {
                return await _api.PostAsync<LeadActivityDto>($"/api/leads/{dto.LeadId}/activities", dto);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating activity for lead {LeadId}", dto.LeadId);
                throw;
            }
        }

        public async Task<List<LeadActivityDto>> GetActivitiesAsync(Guid tenantId, Guid leadId)
        {
            try
            {
                // ✅ FIXED: Add tenantId query parameter
                return await _api.GetAsync<List<LeadActivityDto>>($"/api/leads/{leadId}/activities?tenantId={tenantId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting activities for lead {LeadId}", leadId);
                throw;
            }
        }

        // ==================== REMINDERS ====================

        public async Task<LeadReminderDto> CreateReminderAsync(CreateLeadReminderDto dto)
        {
            try
            {
                return await _api.PostAsync<LeadReminderDto>($"/api/leads/{dto.LeadId}/reminders", dto);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating reminder for lead {LeadId}", dto.LeadId);
                throw;
            }
        }

        public async Task<List<LeadReminderDto>> GetRemindersAsync(Guid tenantId, Guid leadId)
        {
            try
            {
                // ✅ FIXED: Add tenantId query parameter
                return await _api.GetAsync<List<LeadReminderDto>>($"/api/leads/{leadId}/reminders?tenantId={tenantId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting reminders for lead {LeadId}", leadId);
                throw;
            }
        }

        public async Task CompleteReminderAsync(Guid tenantId, Guid reminderId)
        {
            try
            {
                // ✅ FIXED: Add tenantId query parameter
                await _api.PatchVoidAsync($"/api/leads/reminders/{reminderId}/complete?tenantId={tenantId}", null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error completing reminder {ReminderId}", reminderId);
                throw;
            }
        }

        // ==================== ASSIGNMENT & TIMELINE ====================

        public async Task AssignAsync(AssignLeadDto dto)
        {
            try
            {
                await _api.PatchVoidAsync($"/api/leads/{dto.LeadId}/assign", dto);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error assigning lead {LeadId}", dto.LeadId);
                throw;
            }
        }

        public async Task<List<TimelineItemDto>> GetTimelineAsync(Guid tenantId, Guid leadId)
        {
            try
            {
                // ✅ FIXED: Add tenantId query parameter
                return await _api.GetAsync<List<TimelineItemDto>>($"/api/leads/{leadId}/timeline?tenantId={tenantId}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting timeline for lead {LeadId}", leadId);
                throw;
            }
        }

        // ==================== EXPORT/IMPORT ====================

        public async Task<byte[]> ExportAsync(Guid tenantId, string? search, string? status, string? assignedTo)
        {
            try
            {
                var queryParams = new List<string>();
                if (!string.IsNullOrWhiteSpace(search))
                    queryParams.Add($"search={Uri.EscapeDataString(search)}");
                if (!string.IsNullOrWhiteSpace(status))
                    queryParams.Add($"status={status}");
                if (!string.IsNullOrWhiteSpace(assignedTo))
                    queryParams.Add($"assignedTo={Uri.EscapeDataString(assignedTo)}");

                var query = string.Join("&", queryParams);

                // 066: WAS GetAsync<byte[]>.
                //
                // GetAsync<T> reads the response and runs it through
                // System.Text.Json. The export endpoint returns an Excel
                // file — raw bytes, Content-Type
                // application/vnd.openxmlformats-…, not JSON — so the
                // deserializer was being handed a ZIP archive and asked to
                // parse it as a document. The first byte of every .xlsx is
                // 'P' (from "PK"), which is not a valid JSON token, so it
                // threw before a single row reached the user.
                //
                // GetBytesAsync returns the body untouched, which is what
                // IApiService added it for and what DownloadPdfAsync in
                // QuoteService has always used.
                return await _api.GetBytesAsync($"/api/leads/export?{query}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error exporting leads");
                throw;
            }
        }

        //public async Task<ImportLeadsResult> ImportAsync(Guid tenantId, IFormFile file, string? importedBy)
        //{
        //    try
        //    {
        //        using var stream = new MemoryStream();
        //        await file.CopyToAsync(stream);
        //        var fileBytes = stream.ToArray();

        //        var request = new
        //        {
        //            tenantId = tenantId,
        //            fileName = file.FileName,
        //            fileContent = Convert.ToBase64String(fileBytes),
        //            importedBy = importedBy
        //        };

        //        return await _api.PostAsync<ImportLeadsResult>("/api/leads/import", request);
        //    }
        //    catch (Exception ex)
        //    {
        //        _logger.LogError(ex, "Error importing leads");
        //        throw;
        //    }
        //}
    }
}