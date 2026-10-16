// =====================================================================
// FILE: MerkaiTrial.Admin.Web/Services/Core/IApiService.cs
//
// 090b — GetFileAsync: a file WITH its name and content type, for the
// attachment download page. GetBytesAsync throws on any failure and drops
// both, which is fine for a PDF this app names itself but not for a file
// a person uploaded.
// =====================================================================

namespace MerkaiTrial.Admin.Web.Services.Core
{
    public interface IApiService
    {
        Task<T> GetAsync<T>(string url);
        Task<T> PostAsync<T>(string url, object? payload);
        Task PostVoidAsync(string url, object? payload);
        Task PutVoidAsync(string url, object? payload);
        Task PatchVoidAsync(string url, object? payload);
        Task DeleteAsync(string url);
        Task<T> PutAsync<T>(string url, object? payload);
        Task<T> PostMultipartAsync<T>(string url, MultipartFormDataContent content);
        Task<byte[]> GetBytesAsync(string url);

        /// <summary>
        /// 090b. A file with its name and type, for passing straight on to
        /// the browser (attachment downloads). Null when the API answers 404
        /// or 403 — "not yours, or not there", which the caller shows as not
        /// found. Any other failure throws, as the other methods do.
        /// </summary>
        Task<ApiFile?> GetFileAsync(string url);
    }

    /// <summary>090b. A downloaded file, as the API described it.</summary>
    public sealed record ApiFile(byte[] Content, string ContentType, string FileName);
}
