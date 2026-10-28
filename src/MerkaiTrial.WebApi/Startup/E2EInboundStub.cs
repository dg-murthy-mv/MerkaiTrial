// =====================================================================
// FILE: MerkaiTrial.WebApi/Startup/E2EInboundStub.cs
//
// NEW FILE (106 — VUL-25). FOR THE END-TO-END TEST SERVERS ONLY.
//
// A received email arrives in two steps: Resend's webhook says "an email
// came for you" (sender, subject, id), and the worker then DOWNLOADS the
// body, headers, sender checks and attachments from Resend's API. The
// test servers have no Resend key — by rule, they never get email keys —
// so the second step could never run there, and nothing about how a
// received email is filed, checked or shown could be tested.
//
// This stand-in takes Resend's place for the download, and nothing else:
//
//   • ON ONLY when BOTH the environment is Development AND
//     Diagnostics:FakeInboundFolder is set (run-e2e.ps1 sets it for the
//     test WebApi; nothing else does). Agreed in 106.
//   • The test writes what Resend would have answered — one JSON file per
//     email, {folder}/{email id}.json, in Resend's own shape — and the
//     attachments' bytes beside it in {folder}/files/. The "download link"
//     of an attachment is https://fake-inbound.invalid/{file name}; only
//     the file name is used, and only inside {folder}/files/.
//   • It is not a key, it talks to nobody, and it cannot receive real
//     mail: the webhook still needs this run's signing secret.
//
// Everything after the download — sorting, the sender checks, saving the
// attachments, showing the email — is the real code.
// =====================================================================

using System.Text.Json;
using System.Text.RegularExpressions;
using MerkaiTrial.Application.Services.Inbound;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MerkaiTrial.WebApi.Startup;

public static class E2EInboundStub
{
    public const string FolderSetting = "Diagnostics:FakeInboundFolder";

    public static IServiceCollection AddE2EInboundStub(
        this IServiceCollection services, IConfiguration config, IHostEnvironment env)
    {
        var folder = config[FolderSetting];
        if (!env.IsDevelopment() || string.IsNullOrWhiteSpace(folder)) return services;

        Directory.CreateDirectory(Path.Combine(folder, "files"));

        services.RemoveAll<IResendInboundClient>();
        services.AddSingleton<IResendInboundClient>(sp =>
            new FolderInboundClient(folder, sp.GetRequiredService<ILogger<FolderInboundClient>>()));

        Serilog.Log.Warning("{Setting} is on: received emails are read from {Folder} instead of Resend (E2E servers only)",
            FolderSetting, folder);
        return services;
    }
}

/// <summary>106. Resend's received-mail API, answered from files. See the file header.</summary>
public sealed class FolderInboundClient : IResendInboundClient
{
    private static readonly Regex SafeId = new("^[A-Za-z0-9_-]{1,100}$", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly string _folder;
    private readonly ILogger<FolderInboundClient> _logger;

    public FolderInboundClient(string folder, ILogger<FolderInboundClient> logger)
    {
        _folder = folder;
        _logger = logger;
    }

    public bool IsEnabled => true;

    public async Task<ResendCallResult<ResendReceivedEmail>> GetEmailAsync(string emailId, CancellationToken ct = default)
    {
        var path = EmailFile(emailId);
        if (path is null || !File.Exists(path))
            return ResendCallResult<ResendReceivedEmail>.Permanent("No such email in the E2E folder.");

        await using var stream = File.OpenRead(path);
        var email = await JsonSerializer.DeserializeAsync<ResendReceivedEmail>(stream, Json, ct);
        return email is null
            ? ResendCallResult<ResendReceivedEmail>.Permanent("The E2E email file is empty.")
            : ResendCallResult<ResendReceivedEmail>.Success(email);
    }

    public async Task<ResendCallResult<List<ResendAttachmentInfo>>> ListAttachmentsAsync(string emailId, CancellationToken ct = default)
    {
        var got = await GetEmailAsync(emailId, ct);
        return got.Ok
            ? ResendCallResult<List<ResendAttachmentInfo>>.Success(got.Value!.Attachments ?? new List<ResendAttachmentInfo>())
            : ResendCallResult<List<ResendAttachmentInfo>>.Permanent(got.Error ?? "Not found.");
    }

    public async Task<ResendCallResult<byte[]>> DownloadAsync(string downloadUrl, long maxBytes, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return ResendCallResult<byte[]>.Permanent("The attachment link was not a secure web address.");

        // Only the last part of the path, and only inside files/.
        var name = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
        if (string.IsNullOrEmpty(name) || !SafeId.IsMatch(Path.GetFileNameWithoutExtension(name)))
            return ResendCallResult<byte[]>.Permanent("Not an E2E attachment.");

        var path = Path.Combine(_folder, "files", name);
        if (!File.Exists(path)) return ResendCallResult<byte[]>.Permanent("No such E2E attachment.");

        var info = new FileInfo(path);
        if (info.Length > maxBytes) return ResendCallResult<byte[]>.Permanent("The file is larger than the limit.");

        _logger.LogInformation("E2E stand-in served attachment {Name} ({Bytes} bytes)", name, info.Length);
        return ResendCallResult<byte[]>.Success(await File.ReadAllBytesAsync(path, ct));
    }

    private string? EmailFile(string emailId)
        => SafeId.IsMatch(emailId ?? string.Empty) ? Path.Combine(_folder, emailId + ".json") : null;
}
