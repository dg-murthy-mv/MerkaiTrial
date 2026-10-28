// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Infrastructure/ReceivedMail.cs
//
// NEW (106 — VUL-25). Sends the test WebApi a received email, the way
// Resend does, in its two steps:
//
//   1. writes what Resend's API would answer for the email — body, sender
//      checks, attachments — into the stand-in's folder (run-e2e.ps1:
//      Diagnostics__FakeInboundFolder, told to the tests as
//      E2E_INBOUND_DIR), and the attachments' bytes beside it;
//   2. posts the signed "email.received" webhook (this run's secret,
//      Webhooks.cs), exactly as Resend would.
//
// The WebApi's worker then fetches step 1 through the stand-in and runs
// the real processing. WaitAsync watches the database until it is done.
// =====================================================================

using System.Text.Json;

namespace MerkaiTrial.E2E.Infrastructure;

public static class ReceivedMail
{
    /// <summary>The made-up receiving domain run-e2e.ps1 gives the test WebApi.</summary>
    public const string Domain = "inbound.e2e.invalid";

    public sealed record File(string FileName, string ContentType, byte[] Bytes, string Disposition = "attachment");

    public sealed record Auth(string Spf, string Dkim, string Dmarc)
    {
        public static readonly Auth Pass = new("pass", "pass", "pass");
        public static readonly Auth Spoofed = new("fail", "fail", "fail");
    }

    public static string Folder
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("E2E_INBOUND_DIR");
            if (string.IsNullOrWhiteSpace(dir))
                dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "e2e-logs", "inbound"));
            if (!Directory.Exists(dir))
                throw new AssertionException(
                    $"The received-email folder {dir} is missing. Run the tests through run-e2e.ps1, which makes it " +
                    "and tells the test WebApi to read received email from it.");
            return dir;
        }
    }

    /// <summary>Writes the email for the stand-in, then posts the signed webhook. Returns the email's id and the webhook's answer.</summary>
    public static async Task<(string EmailId, ApiResponse Webhook)> SendAsync(
        string fromAddress, string fromName, IReadOnlyList<string> to, string subject, string html, string text,
        Auth auth, IReadOnlyList<File>? files = null)
    {
        var id = "e2e_rcv_" + Guid.NewGuid().ToString("N");
        var folder = Folder;
        Directory.CreateDirectory(Path.Combine(folder, "files"));

        var attachments = new List<object>();
        foreach (var f in files ?? Array.Empty<File>())
        {
            var fileId = "f" + Guid.NewGuid().ToString("N");
            await System.IO.File.WriteAllBytesAsync(Path.Combine(folder, "files", fileId + ".bin"), f.Bytes);
            attachments.Add(new Dictionary<string, object?>
            {
                ["id"] = fileId,
                ["filename"] = f.FileName,
                ["content_type"] = f.ContentType,
                ["content_disposition"] = f.Disposition,
                ["size"] = f.Bytes.LongLength,
                ["download_url"] = $"https://fake-inbound.invalid/{fileId}.bin",
            });
        }

        var from = $"{fromName} <{fromAddress}>";
        var now = DateTime.UtcNow.ToString("O");

        var full = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["from"] = from,
            ["to"] = to,
            ["received_for"] = to,
            ["subject"] = subject,
            ["html"] = html,
            ["text"] = text,
            ["message_id"] = $"<{id}@e2e.invalid>",
            ["created_at"] = now,
            ["headers"] = new Dictionary<string, string> { ["message-id"] = $"<{id}@e2e.invalid>" },
            ["authentication"] = new Dictionary<string, string> { ["spf"] = auth.Spf, ["dkim"] = auth.Dkim, ["dmarc"] = auth.Dmarc },
            ["attachments"] = attachments,
        };
        await System.IO.File.WriteAllTextAsync(Path.Combine(folder, id + ".json"), JsonSerializer.Serialize(full));

        var webhook = JsonSerializer.Serialize(new
        {
            type = "email.received",
            created_at = now,
            data = new { email_id = id, from, to, received_for = to, subject, message_id = $"<{id}@e2e.invalid>", created_at = now },
        });
        var answer = await Webhooks.PostAsync(Webhooks.Signed(webhook));
        return (id, answer);
    }

    /// <summary>Waits until the worker has finished with the email (Done or Failed), up to <paramref name="timeout"/>.</summary>
    public static async Task<TestDatabase.StoredInbound?> WaitAsync(TestDatabase db, string emailId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        TestDatabase.StoredInbound? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await db.InboundAsync(emailId);
            if (last is { ProcessingState: 2 or 4 }) return last;     // Done, Failed
            await Task.Delay(1000);
        }
        return last;
    }
}
