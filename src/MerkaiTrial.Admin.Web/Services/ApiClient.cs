using AspNetCoreGeneratedDocument;
using MerkaiTrial.Application.DTOs;
using MerkaiTrial.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MerkaiTrial.Admin.Web.Services
{
    public class ApiClient
    {
        private readonly HttpClient _http;

        // JSON options for all (de)serialization
        private static readonly JsonSerializerOptions _json = new()
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() }
        };

        public ApiClient(HttpClient http) => _http = http;

        // ---------- Low-level helpers (centralized error handling) ----------

        private static async Task<T> ReadOrThrow<T>(HttpResponseMessage res, string op)
        {
            var body = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode)
                throw new HttpRequestException($"{op} {res.StatusCode}: {body}");
            try
            {
                return JsonSerializer.Deserialize<T>(body, _json)!;
            }
            catch (JsonException jx)
            {
                throw new HttpRequestException($"{op} deserialization failed.", jx);
            }
        }
        /// <summary>
        /// Get single lead detail
        /// </summary>
        public Task<LeadDetailDto> GetLeadDetailAsync(Guid tenantId, Guid leadId)
        {
            return SafeGet<LeadDetailDto>(
                $"api/leads/{leadId}?tenantId={tenantId}",
                "Get lead detail"
            );
        }
        /// <summary>
        /// Update lead details (contact info, channel, source, score)
        /// </summary>
        public Task<LeadDetailDto> UpdateLeadAsync(UpdateLeadDto dto)
        {
            return SafePut<LeadDetailDto>(
                $"api/leads/{dto.LeadId}",
                dto,
                "Update lead"
            );
        }
        /// <summary>
        /// Soft delete lead
        /// </summary>
        public Task DeleteLeadAsync(Guid tenantId, Guid leadId)
        {
            return SafeDelete(
                $"api/leads/{leadId}?tenantId={tenantId}",
                "Delete lead"
            );
        }
        /// <summary>
        /// Update only lead status
        /// </summary>
        public Task UpdateLeadStatusAsync(Guid tenantId, Guid leadId, LeadStatus status)
        {
            var dto = new UpdateLeadStatusDto(tenantId, leadId, status);
            return SafePatchVoid(
                $"api/leads/{leadId}/status",
                dto,
                "Update lead status"
            );
        }
        /// <summary>
        /// Get paginated leads with filtering
        /// </summary>
        public Task<PaginatedLeadsResponse> GetLeadsPaginatedAsync(
            Guid tenantId,
            int page = 1,
            int pageSize = 25,
            string? searchTerm = null)
        {
            var url = $"api/leads/paginated?tenantId={tenantId}&page={page}&pageSize={pageSize}";
            if (!string.IsNullOrWhiteSpace(searchTerm))
                url += $"&search={Uri.EscapeDataString(searchTerm)}";

            return SafeGet<PaginatedLeadsResponse>(url, "Get paginated leads");
        }
        private static HttpRequestException Wrap(string op, Exception ex) =>
            ex switch
            {
                TaskCanceledException => new HttpRequestException($"{op} timed out.", ex),
                JsonException => new HttpRequestException($"{op} deserialization failed.", ex),
                HttpRequestException => new HttpRequestException($"{op} failed: {ex.Message}", ex),
                _ => new HttpRequestException($"{op} failed.", ex)
            };

        private async Task<T> SafeGet<T>(string url, string op)
        {
            try
            {
                var res = await _http.GetAsync(url);
                return await ReadOrThrow<T>(res, op);
            }
            catch (Exception ex) { throw Wrap(op, ex); }
        }

        private async Task<T> SafePost<T>(string url, object? payload, string op)
        {
            try
            {
                var res = await _http.PostAsJsonAsync(url, payload, _json);
                return await ReadOrThrow<T>(res, op);
            }
            catch (Exception ex) { throw Wrap(op, ex); }
        }

        private async Task SafePostVoid(string url, object? payload, string op)
        {
            try
            {
                var res = await _http.PostAsJsonAsync(url, payload, _json);
                var body = await res.Content.ReadAsStringAsync();
                if (!res.IsSuccessStatusCode)
                    throw new HttpRequestException($"{op} {res.StatusCode}: {body}");
            }
            catch (Exception ex) { throw Wrap(op, ex); }
        }

        private async Task<T> SafePut<T>(string url, object? payload, string op)
        {
            try
            {
                var res = await _http.PutAsJsonAsync(url, payload, _json);
                return await ReadOrThrow<T>(res, op);
            }
            catch (Exception ex) { throw Wrap(op, ex); }
        }

        private async Task SafeDelete(string url, string op)
        {
            try
            {
                var res = await _http.DeleteAsync(url);
                var body = await res.Content.ReadAsStringAsync();
                if (!res.IsSuccessStatusCode)
                    throw new HttpRequestException($"{op} {res.StatusCode}: {body}");
            }
            catch (Exception ex) { throw Wrap(op, ex); }
        }

        private async Task SafePatchVoid(string url, object payload, string op)
        {
            try
            {
                var res = await _http.PatchAsJsonAsync(url, payload, _json);
                var body = await res.Content.ReadAsStringAsync();
                if (!res.IsSuccessStatusCode)
                    throw new HttpRequestException($"{op} {res.StatusCode}: {body}");
            }
            catch (Exception ex) { throw Wrap(op, ex); }
        }

        // ---------- Tenants ----------

        public Task<TenantDto[]> GetTenantsAsync() =>
            SafeGet<TenantDto[]>("api/tenants", "Get tenants");

        public async Task<string> GetTenantDefaultCurrencyAsync(Guid tenantId)
        {
            try
            {
                var t = await SafeGet<TenantDto>($"api/tenants/{tenantId}", "Get tenant");
                return t.DefaultCurrency.ToString();
            }
            catch
            {
                // Safe fallback so UI still works
                return "INR";
            }
        }

        // ---------- Leads ----------

        public async Task<LeadDto> CreateLeadAsync(CreateLeadDto dto) =>
            await SafePost<LeadDto>("api/leads", dto, "Create lead");

        public Task<LeadDto[]> GetLeadsByTenantAsync(Guid tenantId) =>
            SafeGet<LeadDto[]>($"api/leads?tenantId={tenantId}", "Get leads by tenant");

        public async Task<LeadListItemDto[]> GetLeadsListAsync(Guid tenantId)
        {
            var resp = await _http.GetAsync($"api/leads?tenantId={tenantId}");
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"Leads API {resp.StatusCode}: {body}");
            return JsonSerializer.Deserialize<LeadListItemDto[]>(body, _json) ?? Array.Empty<LeadListItemDto>();
        }

        public async Task<LeadDto> CreateLeadAsync(Guid tenantId, string fullName, string email, string? phone, string? source)
        {
            var payload = new { TenantId = tenantId, FullName = fullName, Email = email, Phone = phone, Source = source };
            return await SafePost<LeadDto>("api/leads", payload, "Create lead");
        }

        public Task<PagedLeads> GetLeadsAsync(int page = 1, int pageSize = 50) =>
            SafeGet<PagedLeads>($"api/leads?page={page}&pageSize={pageSize}", "Get leads");

        // ---------- Deals / Pipeline ----------

        public async Task<DealDto> CreateDealFromLeadAsync(Guid tenantId, Guid leadId, string title, decimal expectedValue, string currency)
        {
            var payload = new { TenantId = tenantId, LeadId = leadId, Title = title, ExpectedValue = expectedValue, Currency = currency };
            return await SafePost<DealDto>("api/deals", payload, "Create deal from lead");
        }

        public async Task<DealDto> CreateDealAsync(CreateDealDto dto) =>
            await SafePost<DealDto>("api/deals", dto, "Create deal");

        public async Task<DealDto[]> GetDealsAsync(Guid? tenantId = null)
        {
            var url = tenantId.HasValue ? $"api/deals?tenantId={tenantId}" : "api/deals";
            return await SafeGet<DealDto[]>(url, "Get deals");
        }

        public Task MoveDealStageAsync(Guid dealId, string stage) =>
            SafePatchVoid($"api/deals/{dealId}/stage", new { Stage = stage }, "Move deal stage");



       

        // ---------- Invoices / Payments ----------

        public Task<InvoiceDto?> GetInvoiceAsync(string invoiceNumber) =>
            SafeGet<InvoiceDto>($"api/invoices/{invoiceNumber}", "Get invoice");

        public Task<InvoiceSummaryDto[]> GetInvoicesAsync(Guid tenantId) =>
           SafeGet<InvoiceSummaryDto[]>($"api/invoices?tenantId={tenantId}", "Get invoices");

        public async Task MarkInvoicePaidAsync(string number)
        {
            try
            {
                var res = await _http.PostAsync($"api/invoices/{number}/mark-paid", content: null);
                var body = await res.Content.ReadAsStringAsync();
                if (!res.IsSuccessStatusCode)
                    throw new HttpRequestException($"Mark paid {res.StatusCode}: {body}");
            }
            catch (Exception ex)
            {
                throw new HttpRequestException($"Mark invoice paid failed: {ex.Message}", ex);
            }
        }

        public Task<CheckoutResponse> CheckoutAsync(string invoiceNumber, PaymentProviderKind provider) =>
            SafePost<CheckoutResponse>($"api/payments/checkout/{invoiceNumber}", new { Provider = provider }, "Checkout");

        // ---------- Journeys ----------

        public async Task<JourneyDto[]> GetJourneysAsync(Guid tenantId) =>
            await SafeGet<JourneyDto[]>($"api/journeys?tenantId={tenantId}", "Get journeys");

        public Task<JourneyDto> CreateJourneyAsync(Guid tenantId, string name, string stepsJson)
        {
            var payload = new { TenantId = tenantId, Name = name, StepsJson = stepsJson };
            return SafePost<JourneyDto>("api/journeys", payload, "Create journey");
        }

        public Task<JourneyDto> UpdateJourneyAsync(Guid id, Guid tenantId, string name, string stepsJson)
        {
            var payload = new { TenantId = tenantId, Name = name, StepsJson = stepsJson };
            // API returns NoContent, so echo back a DTO
            return SafePut<JourneyDto>($"api/journeys/{id}", payload, "Update journey");
        }

        public Task DeleteJourneyAsync(Guid id, Guid tenantId) =>
            SafeDelete($"api/journeys/{id}?tenantId={tenantId}", "Delete journey");

        // ---------- Contacts / Inbox ----------

        public async Task<ContactDto[]> GetContactsAsync(Guid? tenantId = null)
        {
            if (!tenantId.HasValue)
                throw new ArgumentNullException(nameof(tenantId), "tenantId is required for contacts.");

            return await SafeGet<ContactDto[]>($"api/tenants/{tenantId}/contacts", "Get contacts");
        }

        

        public Task<IEnumerable<ContactLiteDto>> GetContactsLiteAsync(Guid tenantId) =>
            SafeGet<IEnumerable<ContactLiteDto>>($"/api/tenants/{tenantId}/contacts/lite", "Get contacts lite");

        public Task<IEnumerable<InboxMessageDto>> GetInboxAsync(Guid tenantId) =>
            SafeGet<IEnumerable<InboxMessageDto>>($"/api/tenants/{tenantId}/inbox", "Get inbox");

        public Task SendInboxAsync(Guid tenantId, Guid contactId, string text) =>
            SafePostVoid($"api/tenants/{tenantId}/inbox", new { contactId, text }, "Send inbox");

        public Task<LeadListItem[]> GetLeadsByTenantFullAsync(Guid tenantId)
            => _http.GetFromJsonAsync<LeadListItem[]>($"api/leads?tenantId={tenantId}", _json)!;


        private static async Task EnsureSuccess(HttpResponseMessage res, string context)
        {
            if (res.IsSuccessStatusCode) return;

            string body = "";
            try { body = await res.Content.ReadAsStringAsync(); } catch { /* ignore */ }

            var extra = string.IsNullOrWhiteSpace(body) ? "" : $": {body}";
            throw new HttpRequestException($"{context} failed: {(int)res.StatusCode} {res.ReasonPhrase}{extra}");
        }
        // Add these methods to your ApiClient.cs class

        // ==================== LEAD NOTES ====================

        public Task<LeadNoteDto> CreateLeadNoteAsync(CreateLeadNoteDto dto)
        {
            return SafePost<LeadNoteDto>($"api/leads/{dto.LeadId}/notes", dto, "Create lead note");
        }

        public Task<List<LeadNoteDto>> GetLeadNotesAsync(Guid tenantId, Guid leadId)
        {
            return SafeGet<List<LeadNoteDto>>($"api/leads/{leadId}/notes?tenantId={tenantId}", "Get lead notes");
        }

        public Task DeleteLeadNoteAsync(Guid tenantId, Guid noteId)
        {
            return SafeDelete($"api/leads/notes/{noteId}?tenantId={tenantId}", "Delete lead note");
        }

        // ==================== LEAD ACTIVITIES ====================

        public Task<LeadActivityDto> CreateLeadActivityAsync(CreateLeadActivityDto dto)
        {
            return SafePost<LeadActivityDto>($"api/leads/{dto.LeadId}/activities", dto, "Create lead activity");
        }

        public Task<List<LeadActivityDto>> GetLeadActivitiesAsync(Guid tenantId, Guid leadId)
        {
            return SafeGet<List<LeadActivityDto>>($"api/leads/{leadId}/activities?tenantId={tenantId}", "Get lead activities");
        }

        // ==================== LEAD REMINDERS ====================

        public Task<LeadReminderDto> CreateLeadReminderAsync(CreateLeadReminderDto dto)
        {
            return SafePost<LeadReminderDto>($"api/leads/{dto.LeadId}/reminders", dto, "Create lead reminder");
        }

        public Task<List<LeadReminderDto>> GetLeadRemindersAsync(Guid tenantId, Guid leadId)
        {
            return SafeGet<List<LeadReminderDto>>($"api/leads/{leadId}/reminders?tenantId={tenantId}", "Get lead reminders");
        }

        public Task CompleteReminderAsync(Guid tenantId, Guid reminderId)
        {
            return SafePatchVoid($"api/leads/reminders/{reminderId}/complete?tenantId={tenantId}",
                new { }, "Complete reminder");
        }

        // ==================== LEAD ASSIGNMENT ====================

        public Task AssignLeadAsync(AssignLeadDto dto)
        {
            return SafePatchVoid($"api/leads/{dto.LeadId}/assign", dto, "Assign lead");
        }

        // ==================== TIMELINE ====================

        public Task<List<TimelineItemDto>> GetLeadTimelineAsync(Guid tenantId, Guid leadId)
        {
            return SafeGet<List<TimelineItemDto>>($"api/leads/{leadId}/timeline?tenantId={tenantId}", "Get lead timeline");
        }

        // ==================== EXPORT ====================

        public async Task<byte[]> ExportLeadsAsync(
            Guid tenantId,
            string? search = null,
            string? status = null,
            string? assignedTo = null)
        {
            try
            {
                var url = $"api/leads/export?tenantId={tenantId}";
                if (!string.IsNullOrWhiteSpace(search))
                    url += $"&search={Uri.EscapeDataString(search)}";
                if (!string.IsNullOrWhiteSpace(status))
                    url += $"&status={Uri.EscapeDataString(status)}";
                if (!string.IsNullOrWhiteSpace(assignedTo))
                    url += $"&assignedTo={Uri.EscapeDataString(assignedTo)}";

                var response = await _http.GetAsync(url);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsByteArrayAsync();
            }
            catch (Exception ex)
            {
                throw new HttpRequestException("Export leads failed", ex);
            }
        }

        // ==================== IMPORT ====================

        public async Task<ImportLeadsResult> ImportLeadsAsync(Guid tenantId, IFormFile file, string? importedBy = null)
        {
            try
            {
                using var content = new MultipartFormDataContent();
                using var fileStream = file.OpenReadStream();
                using var fileContent = new StreamContent(fileStream);

                fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(file.ContentType);
                content.Add(fileContent, "file", file.FileName);

                var url = $"api/leads/import?tenantId={tenantId}";
                if (!string.IsNullOrWhiteSpace(importedBy))
                    url += $"&importedBy={Uri.EscapeDataString(importedBy)}";

                var response = await _http.PostAsync(url, content);
                return await ReadOrThrow<ImportLeadsResult>(response, "Import leads");
            }
            catch (Exception ex)
            {
                throw new HttpRequestException("Import leads failed", ex);
            }
        }

        // ==================== STATISTICS ====================

        public Task<LeadStatsDto> GetLeadStatsAsync(Guid tenantId)
        {
            return SafeGet<LeadStatsDto>($"api/leads/stats?tenantId={tenantId}", "Get lead stats");
        }

        // Add these methods to your existing ApiClient.cs class

        // ==================== TENANT MANAGEMENT ====================

        /// <summary>
        /// Get paginated tenants with search/filter
        /// </summary>
        public Task<PaginatedTenantsResponse> GetTenantsPaginatedAsync(
            int page = 1,
            int pageSize = 25,
            string? searchTerm = null,
            bool? isActive = null)
        {
            var url = $"api/tenants/paginated?page={page}&pageSize={pageSize}";
            if (!string.IsNullOrWhiteSpace(searchTerm))
                url += $"&search={Uri.EscapeDataString(searchTerm)}";
            if (isActive.HasValue)
                url += $"&isActive={isActive.Value}";

            return SafeGet<PaginatedTenantsResponse>(url, "Get paginated tenants");
        }

        /// <summary>
        /// Get single tenant detail
        /// </summary>
        public Task<TenantDto> GetTenantAsync(Guid tenantId)
        {
            return SafeGet<TenantDto>($"api/tenants/{tenantId}", "Get tenant");
        }

        /// <summary>
        /// Create new tenant
        /// </summary>
        public Task<TenantDto> CreateTenantAsync(CreateTenantCommand command)
        {
            return SafePost<TenantDto>("api/tenants", command, "Create tenant");
        }
        public Task<List<UserRoleDto>> GetUserRolesAsync(Guid tenantId, Guid userId)
        {
            return SafeGet<List<UserRoleDto>>($"api/users/{userId}/roles?tenantId={tenantId}", "Get user roles");
        }
        /// <summary>
        /// Get tenants lookup (for dropdowns)
        /// </summary>
        public Task<List<TenantLookupDto>> GetTenantsLookupAsync()
        {
            return SafeGet<List<TenantLookupDto>>("api/tenants/lookup", "Get tenants lookup");
        }
        /// <summary>
        /// Update tenant
        /// </summary>
        public Task UpdateTenantAsync(UpdateTenantCommand command)
        {
            return SafePutVoid($"api/tenants/{command.TenantId}", command, "Update tenant");
        }

        private async Task SafePutVoid(string url, object? payload, string op)
        {
            try
            {
                var res = await _http.PutAsJsonAsync(url, payload, _json);
                var body = await res.Content.ReadAsStringAsync();
                if (!res.IsSuccessStatusCode)
                    throw new HttpRequestException($"{op} {res.StatusCode}: {body}");
            }
            catch (Exception ex) { throw Wrap(op, ex); }
        }

        /// <summary>
        /// Get tenant settings
        /// </summary>
        public Task<TenantSettingsDto> GetTenantSettingsAsync(Guid tenantId)
        {
            return SafeGet<TenantSettingsDto>($"api/tenants/{tenantId}/settings", "Get tenant settings");
        }

        /// <summary>
        /// Update tenant settings
        /// </summary>
        public Task UpdateTenantSettingsAsync(UpdateTenantSettingsCommand command)
        {
            return SafePutVoid($"api/tenants/{command.TenantId}/settings", command, "Update tenant settings");
        }


        /// <summary>
        /// Get tenant statistics
        /// </summary>
        public Task<TenantStatsDto> GetTenantStatsAsync()
        {
            return SafeGet<TenantStatsDto>("api/tenants/stats", "Get tenant stats");
        }

        // ==================== USER MANAGEMENT ====================

        /// <summary>
        /// Get paginated users with search/filter
        /// </summary>
        public Task<PaginatedUsersResponse> GetUsersPaginatedAsync(Guid tenantId, int page = 1, int pageSize = 25, string? search = null)
        {
            var url = $"api/users/paginated?tenantId={tenantId}&page={page}&pageSize={pageSize}";
            if (!string.IsNullOrWhiteSpace(search))
                url += $"&search={Uri.EscapeDataString(search)}";

            return SafeGet<PaginatedUsersResponse>(url, "Get paginated users");
        }

        /// <summary>
        /// Get single user detail
        /// </summary>
        public Task<UserDto> GetUserAsync(Guid tenantId, Guid userId)
        {
            return SafeGet<UserDto>($"api/users/{userId}?tenantId={tenantId}", "Get user");
        }

        /// <summary>
        /// Create new user
        /// </summary>
        public Task<UserDto> CreateUserAsync(CreateUserCommand command)
        {
            return SafePost<UserDto>("api/users", command, "Create user");
        }

        /// <summary>
        /// Update user
        /// </summary>
        public Task<UserDto> UpdateUserAsync(UpdateUserCommand command)
        {
            return SafePut<UserDto>($"api/users/{command.UserId}", command, "Update user");
        }

        /// <summary>
        /// Update user status (activate/deactivate)
        /// </summary>
        public Task UpdateUserStatusAsync(UpdateUserStatusCommand command)
        {
            return SafePatchVoid($"api/users/{command.UserId}/status", command, "Update user status");
        }

        /// <summary>
        /// Assign roles to user
        /// </summary>
        public Task AssignUserRolesAsync(AssignUserRolesCommand command)
        {
            return SafePostVoid($"api/users/{command.UserId}/roles", command, "Assign user roles");
        }

        /// <summary>
        /// Delete user (soft delete)
        /// </summary>
        public Task DeleteUserAsync(Guid tenantId, Guid userId)
        {
            return SafeDelete($"api/users/{userId}?tenantId={tenantId}", "Delete user");
        }

        /// <summary>
        /// Get user statistics
        /// </summary>
        public Task<UserStatsDto> GetUserStatsAsync(Guid tenantId)
        {
            return SafeGet<UserStatsDto>($"api/users/stats?tenantId={tenantId}", "Get user stats");
        }

        /// <summary>
        /// Get users lookup (for dropdowns)
        /// </summary>
        public Task<List<UserLookupDto>> GetUsersLookupAsync(Guid tenantId, bool activeOnly = true)
        {
            var url = $"api/users/lookup?tenantId={tenantId}&activeOnly={activeOnly}";
            return SafeGet<List<UserLookupDto>>(url, "Get users lookup");
        }

        /// <summary>
        /// Bulk update user status
        /// </summary>
        public Task<BulkOperationResult> BulkUpdateUserStatusAsync(BulkUpdateUserStatusCommand command)
        {
            return SafePost<BulkOperationResult>("api/users/bulk-status", command, "Bulk update user status");
        }

        // ==================== ROLE MANAGEMENT ====================

        // ==================== ROLES METHODS ====================
        // Add these methods to ApiClient.cs (around line ~600 after Tenant methods)

        /// <summary>
        /// Get all roles
        /// </summary>
        public Task<List<RoleListItem>> GetAllRolesAsync()
        {
            return SafeGet<List<RoleListItem>>("api/roles", "Get all roles");
        }

        /// <summary>
        /// Get role by ID
        /// </summary>
        public Task<RoleDto> GetRoleAsync(Guid roleId)
        {
            return SafeGet<RoleDto>($"api/roles/{roleId}", "Get role");
        }

        /// <summary>
        /// Create new role
        /// </summary>
        public Task<RoleDto> CreateRoleAsync(CreateRoleCommand command)
        {
            return SafePost<RoleDto>("api/roles", command, "Create role");
        }

        /// <summary>
        /// Update role
        /// </summary>
        public Task UpdateRoleAsync(UpdateRoleCommand command)
        {
            return SafePutVoid($"api/roles/{command.RoleId}", command, "Update role");
        }

        /// <summary>
        /// Delete role
        /// </summary>
        public Task DeleteRoleAsync(Guid roleId)
        {
            return SafeDelete($"api/roles/{roleId}", "Delete role");
        }

        /// <summary>
        /// Get roles lookup (for dropdowns)
        /// </summary>
        public Task<List<RoleLookupDto>> GetRolesLookupAsync()
        {
            return SafeGet<List<RoleLookupDto>>("api/roles/lookup", "Get roles lookup");
        }

        /// <summary>
        /// Get users assigned to a role
        /// </summary>
        public Task<List<UserLookupDto>> GetRoleUsersAsync(Guid roleId)
        {
            return SafeGet<List<UserLookupDto>>($"api/roles/{roleId}/users", "Get role users");
        }

        /// <summary>
        /// Get role statistics
        /// </summary>
        public Task<RoleStatsDto> GetRoleStatsAsync()
        {
            return SafeGet<RoleStatsDto>("api/roles/stats", "Get role stats");
        }



    }
}
