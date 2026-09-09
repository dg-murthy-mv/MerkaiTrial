using System.Net.Http;
using System.Net.Http.Json;
using System.Net;
using System.Text.Json;

namespace MerkaiTrial.Admin.Web.Services.Core
{
    /// <summary>
    /// Base interface for all API service operations
    /// </summary>
   

    /// <summary>
    /// Base API service with HTTP operations
    /// All modular services (LeadService, UserService, etc.) will use this
    /// </summary>
    public class ApiService : IApiService
    {
        private readonly HttpClient _http;
        private readonly JsonSerializerOptions _json;
        private readonly ILogger<ApiService> _logger;

        public ApiService(HttpClient http, ILogger<ApiService> logger)
        {
            _http = http;
            _logger = logger;
            _json = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
        }


        // ─────────────────────────────────────────────────────────────────
        // Shared response handling.
        //
        // BUG FIXED 2026-08-03: GetAsync/PostAsync/PutAsync each read the body
        // to a string for the error path, then called ReadFromJsonAsync on the
        // *same* response a second time. Two problems:
        //
        //   1. An endpoint returning 204 No Content has an empty body, so
        //      ReadFromJsonAsync threw
        //        "JsonException: The input does not contain any JSON tokens".
        //      That is exactly what broke public quote acceptance: the WebApi
        //      PUT api/quotes/public/{token}/status returned 204, the status
        //      WAS updated, then this method threw and the page model turned
        //      the exception into a 404. Any 204-returning endpoint called
        //      through the generic overloads hit the same wall.
        //
        //   2. The body was read off the wire twice for no reason.
        //
        // Now the body is read once and deserialised from the string, and an
        // empty successful response yields default(T) instead of throwing.
        // ─────────────────────────────────────────────────────────────────
        private T Deserialize<T>(string body, string verb, string url)
        {
            if (string.IsNullOrWhiteSpace(body))
            {
                // 204 No Content, or a 200 with an empty body.
                _logger.LogDebug(
                    "{Verb} {Url} returned success with an empty body; returning default({Type}). " +
                    "If this endpoint never returns content, prefer the *VoidAsync overload.",
                    verb, url, typeof(T).Name);
                return default!;
            }

            return JsonSerializer.Deserialize<T>(body, _json)
                ?? throw new InvalidOperationException(
                    $"{verb} {url} returned a body that deserialised to null.");
        }

        private void ThrowForFailure(HttpResponseMessage response, string body, string verb, string url)
        {
            if (response.IsSuccessStatusCode) return;

            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                var message = ExtractErrorMessage(body);

                if (!string.IsNullOrWhiteSpace(message))
                {
                    // Logged as a warning, not an error: the API worked exactly
                    // as intended and refused something it should refuse.
                    _logger.LogWarning("{Verb} {Url} rejected: {Message}", verb, url, message);
                    throw new InvalidOperationException(message);
                }
            }

            // 403 from TrialActiveActionFilter — the workspace is read-only.
            // Worth its own message: "please try again" is actively misleading
            // when trying again will never work.
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                var message = ExtractErrorMessage(body) ?? ExtractProblemDetail(body);

                if (!string.IsNullOrWhiteSpace(message))
                {
                    _logger.LogWarning("{Verb} {Url} forbidden: {Message}", verb, url, message);
                    throw new InvalidOperationException(message);
                }
            }

            throw new HttpRequestException($"{verb} {url} failed: {response.StatusCode} - {body}");
        }
        private static string? ExtractErrorMessage(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return null;

            try
            {
                using var doc = JsonDocument.Parse(body);

                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("error", out var error)
                    && error.ValueKind == JsonValueKind.String)
                {
                    return error.GetString();
                }
            }
            catch (JsonException)
            {
                // Not JSON — a plain string body, or an HTML error page.
                // Fall through rather than guessing.
            }

            return null;
        }

        /// <summary>Reads ProblemDetails.detail — what TrialActiveActionFilter returns.</summary>
        private static string? ExtractProblemDetail(string body)
        {
            if (string.IsNullOrWhiteSpace(body)) return null;

            try
            {
                using var doc = JsonDocument.Parse(body);

                if (doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("detail", out var detail)
                    && detail.ValueKind == JsonValueKind.String)
                {
                    return detail.GetString();
                }
            }
            catch (JsonException) { }

            return null;
        }

        public async Task<T> GetAsync<T>(string url)
        {
            try
            {
                var response = await _http.GetAsync(url);
                var body = await response.Content.ReadAsStringAsync();
                
                if (!response.IsSuccessStatusCode)
                    ThrowForFailure(response, body, "GET", url);

                return Deserialize<T>(body, "GET", url);
            }
            catch (InvalidOperationException)
            {
                // Already logged as a warning in ThrowForFailure. This is a
                // rejected request, not a failure of the call.
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GET {Url} failed", url);
                throw;
            }
        }

        public async Task<T> PostAsync<T>(string url, object? payload)
        {
            try
            {
                var response = await _http.PostAsJsonAsync(url, payload, _json);
                var body = await response.Content.ReadAsStringAsync();
                
                if (!response.IsSuccessStatusCode)
                    ThrowForFailure(response, body, "POST", url);

                return Deserialize<T>(body, "POST", url);
            }
            catch (InvalidOperationException)
            {
                // Already logged as a warning in ThrowForFailure. This is a
                // rejected request, not a failure of the call.
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "POST {Url} failed", url);
                throw;
            }
        }

        public async Task<T> PostMultipartAsync<T>(string url, MultipartFormDataContent content)
        {
            var response = await _http.PostAsync(url, content);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<T>(json, _json)!;
        }
        public async Task<T> PutAsync<T>(string url, object? payload)
        {
            try
            {
                var response = await _http.PutAsJsonAsync(url, payload, _json);
                var body = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                    ThrowForFailure(response, body, "PUT", url);

                return Deserialize<T>(body, "PUT", url);
            }
            catch (InvalidOperationException)
            {
                // Already logged as a warning in ThrowForFailure. This is a
                // rejected request, not a failure of the call.
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PUT {Url} failed", url);
                throw;
            }
        }
        public async Task PostVoidAsync(string url, object? payload)
        {
            try
            {
                var response = await _http.PostAsJsonAsync(url, payload, _json);
                var body = await response.Content.ReadAsStringAsync();
                
                if (!response.IsSuccessStatusCode)
                    ThrowForFailure(response, body, "POST", url);
            }
            catch (InvalidOperationException)
            {
                // Already logged as a warning in ThrowForFailure. This is a
                // rejected request, not a failure of the call.
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "POST {Url} failed", url);
                throw;
            }
        }

        public async Task PutVoidAsync(string url, object? payload)
        {
            try
            {
                var response = await _http.PutAsJsonAsync(url, payload, _json);
                var body = await response.Content.ReadAsStringAsync();
                
                if (!response.IsSuccessStatusCode)
                    ThrowForFailure(response, body, "PUT", url);
            }
            catch (InvalidOperationException)
            {
                // Already logged as a warning in ThrowForFailure. This is a
                // rejected request, not a failure of the call.
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PUT {Url} failed", url);
                throw;
            }
        }

        public async Task PatchVoidAsync(string url, object? payload)
        {
            try
            {
                var response = await _http.PatchAsJsonAsync(url, payload, _json);
                var body = await response.Content.ReadAsStringAsync();
                
                if (!response.IsSuccessStatusCode)
                    ThrowForFailure(response, body, "PATCH", url);
            }
            catch (InvalidOperationException)
            {
                // Already logged as a warning in ThrowForFailure. This is a
                // rejected request, not a failure of the call.
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "PATCH {Url} failed", url);
                throw;
            }
        }

        public async Task DeleteAsync(string url)
        {
            try
            {
                var response = await _http.DeleteAsync(url);
                var body = await response.Content.ReadAsStringAsync();
                
                if (!response.IsSuccessStatusCode)
                    ThrowForFailure(response, body, "DELETE", url);
            }
            catch (InvalidOperationException)
            {
                // Already logged as a warning in ThrowForFailure. This is a
                // rejected request, not a failure of the call.
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DELETE {Url} failed", url);
                throw;
            }
        }
        public async Task<byte[]> GetBytesAsync(string url)
        {
            var response = await _http.GetAsync(url);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync();
        }
    }
}
