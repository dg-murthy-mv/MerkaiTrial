// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Infrastructure/TestDatabase.cs
//
// The only direct database access the tests have. Everything else goes
// through the browser.
//
//   1. A safety check: refuse to touch any database whose name does not
//      end in _E2E. One wrong connection string must not be able to reset
//      a password in the real database.
//   2. Give the super admin a known password — in the TEST copy only — so
//      the run can create its workspaces through Admin → Tenants → Create.
//   3. Read an account email out of the outbox. Nothing is emailed during
//      a test run (the servers have no email key), so the reset link that
//      would have been in someone's inbox is read from the queued row.
//   4. (088) READ-ONLY counts the screens cannot give exactly: how many
//      contacts have this email, how many companies have this name, and
//      a deal's id from its title (the pipeline board has no stable
//      selector to click through). Nothing in 4 writes.
//   5. (089) READ-ONLY notification checks: how many notifications a
//      person was sent about something, whether one has been read, and how
//      many notification emails were queued to an address. Nothing in 5
//      writes either.
//   6. (092) READ-ONLY lead lookups for the import tests: a lead's id from
//      its name, and the custom field values stored on it, by field name —
//      what the importer actually wrote, which a screen would only show
//      formatted. Nothing in 6 writes.
//   7. (093) READ-ONLY merge checks: whether a lead is deleted, its
//      estimated value, and how many activities on a record have a subject
//      containing some text. Nothing in 7 writes.
// =====================================================================

using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;

namespace MerkaiTrial.E2E.Infrastructure;

public sealed class TestDatabase
{
    /// <summary>NotificationEventType.AccountSecurity (round 086).</summary>
    private const int AccountSecurityEventType = 11;

    /// <summary>What OutboundMessageWorker writes over an account email
    /// after it has really been sent (086).</summary>
    private const string WipedMarker = "Removed after sending";

    private readonly string _connectionString;

    public TestDatabase(string connectionString) => _connectionString = connectionString;

    public async Task<string> EnsureIsE2EDatabaseAsync()
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand("SELECT DB_NAME()", conn);
        var name = (string?)await cmd.ExecuteScalarAsync() ?? "";

        if (!name.EndsWith("_E2E", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Refusing to run: the test database is '{name}'. The end-to-end tests change " +
                "passwords and create workspaces, so they only run against a database whose " +
                "name ends in _E2E (for example MerkaiTrial_E2E). Check E2E_DB / run-e2e.ps1.");

        return name;
    }

    /// <summary>
    /// Picks the first live super admin in the TEST database, gives it the
    /// known E2E password and clears any lockout. Returns its email.
    /// </summary>
    public async Task<string> PrepareSuperAdminAsync(string password)
    {
        await using var conn = await OpenAsync();

        string? email;
        Guid id;
        await using (var find = new SqlCommand(
            @"SELECT TOP 1 Id, Email FROM Users
              WHERE IsSuperAdmin = 1 AND IsDeleted = 0
              ORDER BY CreatedAtUtc", conn))
        await using (var r = await find.ExecuteReaderAsync())
        {
            if (!await r.ReadAsync())
                throw new InvalidOperationException(
                    "The test database has no super admin account. It should be a copy of your " +
                    "development database, which has yours. See SETUP.md, step 2.");
            id = r.GetGuid(0);
            email = r.GetString(1);
        }

        var hash = new PasswordHasher<object>().HashPassword(new object(), password);

        await using var update = new SqlCommand(
            @"UPDATE Users SET
                  PasswordHash        = @hash,
                  SecurityStamp       = @stamp,
                  MustChangePassword  = 0,
                  FailedLoginCount    = 0,
                  LockoutEndUtc       = NULL,
                  IsActive            = 1,
                  EmailConfirmedAtUtc = COALESCE(EmailConfirmedAtUtc, SYSUTCDATETIME()),
                  UpdatedAtUtc        = SYSUTCDATETIME(),
                  UpdatedBy           = 'e2e-setup'
              WHERE Id = @id", conn);
        update.Parameters.AddWithValue("@hash", hash);
        update.Parameters.AddWithValue("@stamp", Guid.NewGuid().ToString("N"));
        update.Parameters.AddWithValue("@id", id);
        await update.ExecuteNonQueryAsync();

        return email;
    }

    /// <summary>How many account emails (invite, reset, password changed)
    /// were queued for this address since the given time.</summary>
    public async Task<int> CountAccountEmailsAsync(string toAddress, DateTime sinceUtc)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            @"SELECT COUNT(*) FROM OutboundMessages
              WHERE ToAddress = @to AND EventType = @type AND CreatedAtUtc >= @since", conn);
        cmd.Parameters.AddWithValue("@to", toAddress);
        cmd.Parameters.AddWithValue("@type", AccountSecurityEventType);
        cmd.Parameters.AddWithValue("@since", sinceUtc);
        return (int)(await cmd.ExecuteScalarAsync() ?? 0);
    }

    /// <summary>
    /// Waits for an account email to this address and returns the
    /// SetPassword link inside it. Fails loudly if the email was really
    /// SENT — that would mean the test servers have a live email key.
    /// </summary>
    public async Task<string> WaitForSetPasswordLinkAsync(string toAddress, DateTime sinceUtc, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (true)
        {
            await using (var conn = await OpenAsync())
            await using (var cmd = new SqlCommand(
                @"SELECT TOP 1 BodyText, BodyHtml FROM OutboundMessages
                  WHERE ToAddress = @to AND EventType = @type AND CreatedAtUtc >= @since
                  ORDER BY CreatedAtUtc DESC", conn))
            {
                cmd.Parameters.AddWithValue("@to", toAddress);
                cmd.Parameters.AddWithValue("@type", AccountSecurityEventType);
                cmd.Parameters.AddWithValue("@since", sinceUtc);

                await using var r = await cmd.ExecuteReaderAsync();
                if (await r.ReadAsync())
                {
                    var text = r.IsDBNull(0) ? "" : r.GetString(0);
                    var html = r.IsDBNull(1) ? "" : r.GetString(1);

                    if (text.Contains(WipedMarker) || html.Contains(WipedMarker))
                        throw new InvalidOperationException(
                            "The account email was actually SENT during the test run (the worker wiped " +
                            "its text after sending). The test servers must not have an email key — " +
                            "start them with run-e2e.ps1, which blanks Email__ApiKey.");

                    var link = FindSetPasswordLink(text) ?? FindSetPasswordLink(html);
                    if (link is not null) return link;

                    throw new InvalidOperationException(
                        $"An account email to {toAddress} was queued but contains no SetPassword link.");
                }
            }

            if (DateTime.UtcNow > deadline)
                throw new System.TimeoutException(
                    $"No account email to {toAddress} appeared in the outbox within {timeout.TotalSeconds:0}s.");

            await Task.Delay(500);
        }
    }

    /// <summary>Finds "/Account/SetPassword?token=…" in text or HTML.</summary>
    public static string? FindSetPasswordLink(string? content)
    {
        if (string.IsNullOrEmpty(content)) return null;
        var m = Regex.Match(content, @"(https?://[^\s""'<>]+)?/Account/SetPassword\?[^\s""'<>]+",
            RegexOptions.IgnoreCase);
        // Trailing punctuation belongs to the sentence, not the link.
        return m.Success ? System.Net.WebUtility.HtmlDecode(m.Value).TrimEnd('.', ',', ')', ']') : null;
    }

    // ── 088: read-only lookups ────────────────────────────────────────

    /// <summary>The workspace's id, from its (unique, time-stamped) name.</summary>
    public async Task<Guid> TenantIdAsync(string tenantName)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand("SELECT TOP 1 Id FROM Tenants WHERE Name = @n", conn);
        cmd.Parameters.AddWithValue("@n", tenantName);
        var id = await cmd.ExecuteScalarAsync();
        return id is Guid g ? g : throw new InvalidOperationException($"No workspace called '{tenantName}' in the test database.");
    }

    /// <summary>Live (not deleted) contacts in a workspace with this email.</summary>
    public Task<int> CountContactsAsync(Guid tenantId, string email) =>
        CountAsync("SELECT COUNT(*) FROM Contacts WHERE TenantId = @t AND Email = @v AND IsDeleted = 0", tenantId, email);

    /// <summary>Live companies in a workspace with this exact name.</summary>
    public Task<int> CountCompaniesAsync(Guid tenantId, string name) =>
        CountAsync("SELECT COUNT(*) FROM Companies WHERE TenantId = @t AND Name = @v AND IsDeleted = 0", tenantId, name);

    /// <summary>The contact a converted deal points at, and that contact's company.</summary>
    public async Task<(Guid? ContactId, Guid? CompanyId)> DealContactAsync(Guid dealId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            @"SELECT TOP 1 c.Id, c.CompanyId FROM Deals d
              LEFT JOIN Contacts c ON c.Id = d.ContactId
              WHERE d.Id = @d", conn);
        cmd.Parameters.AddWithValue("@d", dealId);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return (null, null);
        return (r.IsDBNull(0) ? null : r.GetGuid(0), r.IsDBNull(1) ? null : r.GetGuid(1));
    }

    /// <summary>088b. The company on the DEAL row itself (Deals.CompanyId),
    /// which every deal screen reads — not the contact's company.</summary>
    public async Task<Guid?> DealCompanyIdAsync(Guid dealId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand("SELECT CompanyId FROM Deals WHERE Id = @d", conn);
        cmd.Parameters.AddWithValue("@d", dealId);
        return await cmd.ExecuteScalarAsync() is Guid g ? g : null;
    }

    /// <summary>A deal's id from its title, waiting briefly for it to appear.</summary>
    public async Task<Guid> DealIdByTitleAsync(Guid tenantId, string title)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            await using (var conn = await OpenAsync())
            await using (var cmd = new SqlCommand(
                "SELECT TOP 1 Id FROM Deals WHERE TenantId = @t AND Title = @v AND IsDeleted = 0", conn))
            {
                cmd.Parameters.AddWithValue("@t", tenantId);
                cmd.Parameters.AddWithValue("@v", title);
                if (await cmd.ExecuteScalarAsync() is Guid id) return id;
            }

            if (DateTime.UtcNow > deadline)
                throw new InvalidOperationException($"No deal titled '{title}' was saved.");
            await Task.Delay(500);
        }
    }

    // ── 089: notifications (read-only) ─────────────────────────────────

    /// <summary>
    /// In-app notifications for the person with this email whose title
    /// contains <paramref name="titleContains"/>, dismissed ones included.
    /// A notification is written in the same transaction as the change
    /// that raised it, so once that save has returned this is final.
    /// </summary>
    public async Task<int> CountNotificationsAsync(string recipientEmail, string titleContains)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            @"SELECT COUNT(*) FROM Notifications n
              JOIN Users u ON u.Id = n.RecipientUserId
              WHERE u.Email = @e AND CHARINDEX(@t, n.Title) > 0", conn);
        cmd.Parameters.AddWithValue("@e", recipientEmail);
        cmd.Parameters.AddWithValue("@t", titleContains);
        return (int)(await cmd.ExecuteScalarAsync() ?? 0);
    }

    /// <summary>True once the notification has been marked read.</summary>
    public async Task<bool> IsNotificationReadAsync(Guid notificationId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT CASE WHEN ReadAtUtc IS NULL THEN 0 ELSE 1 END FROM Notifications WHERE Id = @id", conn);
        cmd.Parameters.AddWithValue("@id", notificationId);
        var v = await cmd.ExecuteScalarAsync();
        if (v is null) throw new InvalidOperationException($"No notification {notificationId} in the test database.");
        return Convert.ToInt32(v) == 1;
    }

    /// <summary>
    /// Emails of one notification type (NotificationEventType as a number,
    /// e.g. 6 = LeadAssigned) queued to this address since the given time,
    /// waiting up to <paramref name="waitSeconds"/> for at least
    /// <paramref name="atLeast"/> to appear. Returns the count found.
    /// </summary>
    public async Task<int> CountQueuedEmailsAsync(string toAddress, int eventType, DateTime sinceUtc,
        int atLeast = 0, int waitSeconds = 0)
    {
        var deadline = DateTime.UtcNow.AddSeconds(waitSeconds);
        while (true)
        {
            int n;
            await using (var conn = await OpenAsync())
            await using (var cmd = new SqlCommand(
                @"SELECT COUNT(*) FROM OutboundMessages
                  WHERE ToAddress = @to AND EventType = @type AND CreatedAtUtc >= @since", conn))
            {
                cmd.Parameters.AddWithValue("@to", toAddress);
                cmd.Parameters.AddWithValue("@type", eventType);
                cmd.Parameters.AddWithValue("@since", sinceUtc);
                n = (int)(await cmd.ExecuteScalarAsync() ?? 0);
            }

            if (n >= atLeast || DateTime.UtcNow > deadline) return n;
            await Task.Delay(500);
        }
    }

    // ── 092: leads and their custom values (read-only) ────────────────

    /// <summary>A live lead's id from its exact name; null when there is none.</summary>
    public async Task<Guid?> LeadIdByNameAsync(Guid tenantId, string fullName)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT TOP 1 Id FROM Leads WHERE TenantId = @t AND FullName = @v AND IsDeleted = 0 ORDER BY CreatedAtUtc DESC", conn);
        cmd.Parameters.AddWithValue("@t", tenantId);
        cmd.Parameters.AddWithValue("@v", fullName);
        return await cmd.ExecuteScalarAsync() is Guid g ? g : null;
    }

    /// <summary>Live leads in a workspace with this exact name.</summary>
    public Task<int> CountLeadsAsync(Guid tenantId, string fullName) =>
        CountAsync("SELECT COUNT(*) FROM Leads WHERE TenantId = @t AND FullName = @v AND IsDeleted = 0", tenantId, fullName);

    /// <summary>One stored custom value, in its typed columns.</summary>
    public sealed record StoredValue(string? Text, decimal? Number, DateOnly? Date, bool? Flag);

    /// <summary>The custom field values on one record, keyed by the field's name.</summary>
    public async Task<Dictionary<string, StoredValue>> CustomValuesAsync(Guid entityId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            @"SELECT d.Label, v.TextValue, v.NumberValue, v.DateValue, v.BoolValue
              FROM CustomFieldValues v
              JOIN CustomFieldDefinitions d ON d.Id = v.DefinitionId
              WHERE v.EntityId = @e", conn);
        cmd.Parameters.AddWithValue("@e", entityId);

        var result = new Dictionary<string, StoredValue>(StringComparer.Ordinal);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            result[r.GetString(0)] = new StoredValue(
                r.IsDBNull(1) ? null : r.GetString(1),
                r.IsDBNull(2) ? null : r.GetDecimal(2),
                r.IsDBNull(3) ? null : DateOnly.FromDateTime(r.GetDateTime(3)),
                r.IsDBNull(4) ? null : r.GetBoolean(4));
        }
        return result;
    }

    // ── 093: merge checks (read-only) ─────────────────────────────────

    /// <summary>True when the lead row is soft-deleted; null when there is no such row.</summary>
    public async Task<bool?> IsLeadDeletedAsync(Guid leadId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand("SELECT IsDeleted FROM Leads WHERE Id = @id", conn);
        cmd.Parameters.AddWithValue("@id", leadId);
        return await cmd.ExecuteScalarAsync() is bool b ? b : null;
    }

    /// <summary>A lead's estimated value.</summary>
    public async Task<decimal?> LeadValueAsync(Guid leadId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand("SELECT EstimatedValue FROM Leads WHERE Id = @id", conn);
        cmd.Parameters.AddWithValue("@id", leadId);
        return await cmd.ExecuteScalarAsync() is decimal d ? d : null;
    }

    /// <summary>Live activities and tasks on a record whose subject contains the text.</summary>
    public async Task<int> CountActivitiesAsync(Guid entityId, string subjectContains)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT COUNT(*) FROM Activities WHERE EntityId = @e AND IsDeleted = 0 AND Subject LIKE @s", conn);
        cmd.Parameters.AddWithValue("@e", entityId);
        cmd.Parameters.AddWithValue("@s", "%" + subjectContains.Replace("[", "[[]").Replace("%", "[%]") + "%");
        return (int)(await cmd.ExecuteScalarAsync() ?? 0);
    }

    private async Task<int> CountAsync(string sql, Guid tenantId, string value)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("@t", tenantId);
        cmd.Parameters.AddWithValue("@v", value);
        return (int)(await cmd.ExecuteScalarAsync() ?? 0);
    }

    private async Task<SqlConnection> OpenAsync()
    {
        var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        return conn;
    }
}
