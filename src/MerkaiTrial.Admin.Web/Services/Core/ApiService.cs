// 090b — GetFileAsync (attachment downloads): the file with its name and
// type, null on 404/403. Nothing else in this file changes.
//
// 091 — PostMultipartAsync (every attachment upload: leads, deals, quotes)
//   told people things like "Response status code does not indicate
//   success: 400 (Bad Request)" and "Error while copying content to a
//   stream." Now:
//     • A file over 10 MB is refused HERE, before it is sent:
//       "'huge.pdf' is 11.0 MB. Files can be up to 10 MB." The API stops
//       reading an upload that big partway through, so nothing it could
//       say would ever reach the page.
//     • A refusal from the API (wrong type of file, record not found…)
//       comes back as its own sentence, through ThrowForFailure like every
//       other call here — an InvalidOperationException, which all three
//       pages already show word for word.
//     • An upload cut off on the way says so, instead of naming a stream.

using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MerkaiTrial.Admin.Web.Services.Core
{
    /// <summary>
    /// Base interface for all API service operations
    /// </summary>
   

    /// <summary>
    /// Base API service with HTTP operations
    /// All modular services (LeadService, UserService, etc.) will use this
    /// </summary>
    // 105 — VUL-20: every log line here that names the address now goes
    // through Safe(url): public link tokens masked, query values dropped.
    public class ApiService : IApiService
    {
        // 105 — VUL-20: log lines get the path with any public link token
        // masked and the query reduced to its names — never what was typed.
        private static string Safe(string? url)
        {
            if (string.IsNullOrEmpty(url)) return "(none)";
            try { return HttpLoggingHandler.SafeTarget(new Uri(url, UriKind.RelativeOrAbsolute)); }
            catch (UriFormatException) { return "(address not shown)"; }
        }

        private readonly HttpClient _http;
        private readonly JsonSerializerOptions _json;
        private readonly ILogger<ApiService> _logger;

        public ApiService(HttpClient http, ILogger<ApiService> logger)
        {
            _http = http;
            _logger = logger;
            _json = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,

                // WebApi registers JsonStringEnumConverter in its Program.cs,
                // so it sends enums as "Open" rather than 0. Without the
                // matching converter here, EVERY enum crossing the host
                // boundary fails to deserialise. PipelineStage.Category was
                // the first one to do so — nothing else had crossed as an
                // enum before.
                Converters = { new JsonStringEnumConverter() }
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
                    _logger.LogWarning("{Verb} {Url} rejected: {Message}", verb, Safe(url), message);
                    throw new InvalidOperationException(message);
                }
            }

            // 102 — 409 with { "error": … } from an edit: someone else saved
            // the record after this person's form was loaded. Its own type,
            // so an Edit page can keep what the person typed and say why.
            // (Payments' 409s carry no "error" and fall through as before.)
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                var message = ExtractErrorMessage(body);
                if (!string.IsNullOrWhiteSpace(message))
                {
                    _logger.LogWarning("{Verb} {Url} refused — record changed: {Message}", verb, Safe(url), message);
                    throw new MerkaiTrial.Infrastructure.Persistence.RecordChangedException(message);
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
                    _logger.LogWarning("{Verb} {Url} forbidden: {Message}", verb, Safe(url), message);
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
                _logger.LogError(ex, "GET {Url} failed", Safe(url));
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
                _logger.LogError(ex, "POST {Url} failed", Safe(url));
                throw;
            }
        }

        /// <summary>
        /// 091. The API's own upload limit (RequestSizeLimit on every
        /// attachment endpoint, and LocalFileStorageService). Checked here
        /// first because an upload over it is cut off by the API mid-way,
        /// with no reason the page could show.
        /// </summary>
        public const long MaxUploadBytes = 10 * 1024 * 1024;

        public async Task<T> PostMultipartAsync<T>(string url, MultipartFormDataContent content)
        {
            // 091: too big → say so before sending a byte. ContentLength is
            // known for an uploaded file (its stream can seek); when it is
            // not, the API's limit still applies and the catch below
            // explains what happened.
            foreach (var part in content)
            {
                var size = part.Headers.ContentLength;
                if (size is > MaxUploadBytes)
                {
                    var name = part.Headers.ContentDisposition?.FileNameStar
                            ?? part.Headers.ContentDisposition?.FileName?.Trim('"')
                            ?? "That file";
                    var mb = (size.Value / (1024d * 1024d)).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
                    _logger.LogInformation("Upload to {Url} refused before sending: {Bytes} bytes", Safe(url), size.Value);
                    throw new InvalidOperationException(
                        $"'{name}' is {mb} MB. Files can be up to {MaxUploadBytes / (1024 * 1024)} MB.");
                }
            }

            HttpResponseMessage response;
            try
            {
                response = await _http.PostAsync(url, content);
            }
            catch (HttpRequestException ex)
            {
                // The connection was closed under the upload — in practice
                // the API's size limit when the length was not known above.
                _logger.LogWarning(ex, "Upload to {Url} was cut off", Safe(url));
                throw new InvalidOperationException(
                    "The upload was cut off before it finished. Files can be up to " +
                    $"{MaxUploadBytes / (1024 * 1024)} MB — if this one is smaller, please try again.");
            }

            using (response)
            {
                var body = await response.Content.ReadAsStringAsync();

                if (response.StatusCode == HttpStatusCode.RequestEntityTooLarge)
                    throw new InvalidOperationException(
                        $"That file is too big. Files can be up to {MaxUploadBytes / (1024 * 1024)} MB.");

                if (!response.IsSuccessStatusCode)
                    ThrowForFailure(response, body, "POST", url);

                return Deserialize<T>(body, "POST", url);
            }
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
                _logger.LogError(ex, "PUT {Url} failed", Safe(url));
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
                _logger.LogError(ex, "POST {Url} failed", Safe(url));
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
                _logger.LogError(ex, "PUT {Url} failed", Safe(url));
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
                _logger.LogError(ex, "PATCH {Url} failed", Safe(url));
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
                _logger.LogError(ex, "DELETE {Url} failed", Safe(url));
                throw;
            }
        }
        public async Task<byte[]> GetBytesAsync(string url)
        {
            var response = await _http.GetAsync(url);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync();
        }

        /// <summary>
        /// 090b. See IApiService.GetFileAsync. 404 and 403 come back as null —
        /// the download page turns that into "not found" without saying which,
        /// exactly as the API intends.
        /// </summary>
        public async Task<ApiFile?> GetFileAsync(string url)
        {
            using var response = await _http.GetAsync(url);

            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
                return null;

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                ThrowForFailure(response, body, "GET", url);
            }

            var bytes = await response.Content.ReadAsByteArrayAsync();
            var type  = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";

            var disposition = response.Content.Headers.ContentDisposition;
            var name = disposition?.FileNameStar ?? disposition?.FileName?.Trim('"');
            if (string.IsNullOrWhiteSpace(name)) name = "attachment";

            return new ApiFile(bytes, type, name);
        }
    }
}
