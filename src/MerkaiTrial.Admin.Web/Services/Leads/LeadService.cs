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
//
// 079 — CUSTOM FIELDS ON LEADS
//   • GetPaginatedAsync takes optional custom field filters, sent as
//     repeated ?cf= values, each escaped as a whole (the value part is
//     free text). Optional and LAST, so every existing caller is unchanged.
//   • The STATUS filter is escaped now, on the list and on the export. It
//     went into the URL raw; the Active tab sends a comma-separated key
//     list, and a status key with "&" or "#" in it would have cut the
//     query string short.
//   • Create and Update let an InvalidOperationException through WITHOUT
//     an ERROR log line. That is how the API's 400 { error: "Renewal date
//     must be a valid date." } arrives (IApiService converts it), and a
//     person leaving a required field empty is not a system fault. The
//     page shows the sentence.
//
// 092 — ExportAsync takes the custom field filters too (optional, last),
//   sent as repeated ?cf= exactly like the list, so the Export button
//   downloads the rows on screen. It downloads through GetFileAsync, so a
//   refusal from the API (the row ceiling) arrives as an
//   InvalidOperationException carrying the API's sentence, and is NOT
//   logged as an error — the page shows it.
//
// 096 — GetPaginatedAsync takes the list's sort column and direction
//   (optional, last), sent as ?sort=&dir=. The API checks them against a
//   fixed list; anything else is the default order.
//
// 093 — DUPLICATES AND MERGE
//   • FindDuplicatesAsync: leads that look like the same person (email,
//     phone, or name + company), only among those the user can see.
//     A failure is the CALLER's to swallow — a duplicate check must never
//     stop a lead being saved.
//   • MergeAsync: two leads into one. The API's refusals (converted lead,
//     no delete permission) arrive as InvalidOperationException with the
//     sentence, and are not logged as errors.
// =====================================================================

using MerkaiTrial.Admin.Web.Services.Core;
// 093 — aliases, not the whole namespace: Commands.Leads holds many
// types, and one sharing a name with Application.DTOs would make every
// use of that name ambiguous here.
using LeadDuplicateDto = MerkaiTrial.Application.Commands.Leads.LeadDuplicateDto;
using MergeLeadsDto = MerkaiTrial.Application.Commands.Leads.MergeLeadsDto;
using MergeLeadsResultDto = MerkaiTrial.Application.Commands.Leads.MergeLeadsResultDto;
using MerkaiTrial.Application.Configuration;   // 079 — CustomFieldFilterCodec
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
            string? assignedTo = null,
            IReadOnlyList<CustomFieldFilter>? customFilters = null,   // 079
            string? sort = null,                                      // 096
            string? dir = null);
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
        Task<byte[]> ExportAsync(Guid tenantId, string? search, string? status, string? assignedTo,
            IReadOnlyList<CustomFieldFilter>? customFilters = null);   // 092

        // 093 — duplicates and merge
        Task<List<LeadDuplicateDto>> FindDuplicatesAsync(string? email, string? phone, string? fullName,
            string? companyName, Guid? excludeLeadId = null);
        Task<MergeLeadsResultDto> MergeAsync(MergeLeadsDto dto);
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
            catch (InvalidOperationException)
            {
                throw;   // 079 — a sentence for the person; see the header
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
            catch (InvalidOperationException)
            {
                throw;   // 079 — a sentence for the person; see the header
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
            string? assignedTo = null,
            IReadOnlyList<CustomFieldFilter>? customFilters = null,
            string? sort = null,
            string? dir = null)
        {
            try
            {
                var queryParams = new List<string>
                {
                    $"pageNumber={pageNumber}",
                    $"pageSize={pageSize}"
                };

                // 096 — escaped like every other value; the API ignores a key it does not know.
                if (!string.IsNullOrWhiteSpace(sort)) queryParams.Add($"sort={Uri.EscapeDataString(sort)}");
                if (!string.IsNullOrWhiteSpace(dir))  queryParams.Add($"dir={Uri.EscapeDataString(dir)}");

                if (!string.IsNullOrWhiteSpace(searchTerm))
                    queryParams.Add($"searchTerm={Uri.EscapeDataString(searchTerm.Trim())}");
                if (!string.IsNullOrWhiteSpace(status))
                    queryParams.Add($"status={Uri.EscapeDataString(status)}");          // 079 — was raw
                if (!string.IsNullOrWhiteSpace(assignedTo))
                    queryParams.Add($"assignedTo={Uri.EscapeDataString(assignedTo)}");

                // 079 — escaped as a whole: the value part is free text.
                foreach (var f in customFilters ?? Array.Empty<CustomFieldFilter>())
                    queryParams.Add($"cf={Uri.EscapeDataString(CustomFieldFilterCodec.Encode(f))}");

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

        public async Task<byte[]> ExportAsync(Guid tenantId, string? search, string? status, string? assignedTo,
            IReadOnlyList<CustomFieldFilter>? customFilters = null)
        {
            try
            {
                var queryParams = new List<string>();
                if (!string.IsNullOrWhiteSpace(search))
                    queryParams.Add($"search={Uri.EscapeDataString(search)}");
                if (!string.IsNullOrWhiteSpace(status))
                    queryParams.Add($"status={Uri.EscapeDataString(status)}");          // 079 — was raw
                if (!string.IsNullOrWhiteSpace(assignedTo))
                    queryParams.Add($"assignedTo={Uri.EscapeDataString(assignedTo)}");

                // 092 — the list's custom filters, escaped as a whole like GetPaginatedAsync.
                foreach (var f in customFilters ?? Array.Empty<CustomFieldFilter>())
                    queryParams.Add($"cf={Uri.EscapeDataString(CustomFieldFilterCodec.Encode(f))}");

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
                //
                // 092: GetFileAsync rather than GetBytesAsync. GetBytesAsync
                // throws a bare HttpRequestException on any failure, so the
                // API's "That list has 61,000 leads…" reached the page as
                // "Response status code does not indicate success: 400".
                // GetFileAsync turns a 400 { error } into an
                // InvalidOperationException carrying the sentence, and a
                // 403/404 into null.
                var file = await _api.GetFileAsync($"/api/leads/export?{query}");
                if (file is null)
                    throw new InvalidOperationException("You don't have access to export leads.");
                return file.Content;
            }
            catch (InvalidOperationException)
            {
                throw;   // 092 — the API's own sentence; the page shows it
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error exporting leads");
                throw;
            }
        }

        // ==================== DUPLICATES & MERGE (093) ====================

        public async Task<List<LeadDuplicateDto>> FindDuplicatesAsync(string? email, string? phone, string? fullName,
            string? companyName, Guid? excludeLeadId = null)
        {
            var q = new List<string>();
            if (!string.IsNullOrWhiteSpace(email))       q.Add($"email={Uri.EscapeDataString(email.Trim())}");
            if (!string.IsNullOrWhiteSpace(phone))       q.Add($"phone={Uri.EscapeDataString(phone.Trim())}");
            if (!string.IsNullOrWhiteSpace(fullName))    q.Add($"name={Uri.EscapeDataString(fullName.Trim())}");
            if (!string.IsNullOrWhiteSpace(companyName)) q.Add($"company={Uri.EscapeDataString(companyName.Trim())}");
            if (excludeLeadId is { } ex && ex != Guid.Empty) q.Add($"excludeId={ex}");

            if (q.Count == 0 || (q.Count == 1 && excludeLeadId.HasValue)) return new List<LeadDuplicateDto>();

            return await _api.GetAsync<List<LeadDuplicateDto>>($"/api/leads/duplicates?{string.Join("&", q)}")
                   ?? new List<LeadDuplicateDto>();
        }

        public async Task<MergeLeadsResultDto> MergeAsync(MergeLeadsDto dto)
        {
            try
            {
                return await _api.PostAsync<MergeLeadsResultDto>("/api/leads/merge", dto);
            }
            catch (InvalidOperationException)
            {
                throw;   // the API's own sentence — the page shows it
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error merging lead {Other} into {Keep}", dto.MergeLeadId, dto.KeepLeadId);
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