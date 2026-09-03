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
    }
}
