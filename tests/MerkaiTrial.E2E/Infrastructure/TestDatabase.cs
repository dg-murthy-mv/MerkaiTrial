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
//   8. (094) READ-ONLY: a deal's current stage key, so the phone test
//      can tell that Move on the board really moved it. Nothing in 8
//      writes.
//   9. (095) PrepareOutboxAsync, once before the run, in the _E2E
//      database only: clears out queued messages more than a day old and
//      makes sure the outbox has an index on the address the tests look
//      up. See the method for why.
//  10. (098) Automation: the rule log, a plan's automation limit (read,
//      and put back after a test changes it), and retiring a test's
//      rules so they never fire on later tests' records.
//  11. (099) BackdateLeadAsync — makes a lead look N days old, so a
//      time-based rule ("no activity for 3 days") is due without waiting.
//  13. (101) READ-ONLY: a person's role ids, and an invoice's biggest
//      line discount.
//  12. (100) READ-ONLY, for the security and money tests:
//      • ApiIdentityAsync — who a person is and what their roles allow,
//        read the way SignInService reads it at sign-in, so the test can
//        make the same token Admin.Web would (Api.cs).
//      • RowJsonAsync / CountWhereAsync — a record exactly as stored, and
//        how many rows hang off it, so "workspace B could not change
//        workspace A's record" is checked in the database, not on a page.
//        Table and column names come from fixed lists, never from text.
//      • Conversion, payment, quote and automation read-backs: what one
//        conversion, one payment, one approval or one rule run left
//        behind. Nothing in 12 writes, except DeleteAutomationRunsAsync
//        (one rule's log lines for one record — the "the log line was
//        lost" half of the automation retry test, _E2E only).
//
// 105: sessions (read, and "a day later"), attachments (read, and point a
//      row at another path), a lead's text and a person's first name written
//      past every check, an email made to look sent for the webhook tests,
//      and a person's stored hash and stamp for the secrets test. Every
//      write is _E2E only, like the rest of this file.
//
// 106: text written past every check on contacts and people (line breaks
//      for the email test), what an outgoing email was queued as, a
//      received email and its attachments, audit rows, and trying to
//      change or delete an audit row (always rolled back).
// =====================================================================

using System.Text.Json;
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
    /// 095. Keeps the outbox quick to search during a run. _E2E ONLY.
    ///
    /// WHY. The tests find an email by its address (ToAddress), and the
    /// outbox has no index on that column, so each lookup reads the WHOLE
    /// table. In the test database nothing is ever sent (no email key), so
    /// the worker's 90-day clean-up, which only removes SENT messages,
    /// never removes anything. Every run adds its emails, with their full
    /// HTML bodies, to every run before it. The database-wait watcher
    /// caught the result: a count of one address's emails waiting 10–13
    /// seconds reading pages from disk (PAGEIOLATCH), and on a busier
    /// moment 30, which fails the test.
    ///
    /// So, before the tests start:
    ///   1. queued messages older than a day are deleted, in batches (their
    ///      attachments go with them; the database cascades the delete);
    ///   2. an index on (ToAddress, EventType, CreatedAtUtc) is created if
    ///      it is missing, so a lookup reads a few rows instead of all.
    ///
    /// The index is named IX_E2E_… so it can never be mistaken for one of
    /// the app's own; it exists in the test database only, and only while
    /// the tests run — DropOutboxIndexAsync removes it at the end, so a
    /// later migration that changes the outbox never trips over it.
    /// Neither step can fail the run: a problem is reported and the tests
    /// go ahead.
    /// </summary>
    public async Task PrepareOutboxAsync()
    {
        await EnsureIsE2EDatabaseAsync();

        // 1. Old rows out. Separate from 2 so a refusal here (another table
        //    pointing at an old message, say) still leaves the index made.
        try
        {
            var removed = 0;
            await using var conn = await OpenAsync();
            while (true)
            {
                await using var del = new SqlCommand(
                    @"DELETE TOP (2000) FROM OutboundMessages
                      WHERE CreatedAtUtc < DATEADD(day, -1, SYSUTCDATETIME())", conn)
                { CommandTimeout = 180 };
                var n = await del.ExecuteNonQueryAsync();
                removed += n;
                if (n < 2000) break;
            }

            TestContext.Progress.WriteLine(removed == 0
                ? "[e2e setup] Outbox: nothing older than a day to clear."
                : $"[e2e setup] Outbox: cleared {removed} queued message(s) from earlier runs.");
        }
        catch (Exception ex)
        {
            TestContext.Progress.WriteLine(
                $"[e2e setup] Could not clear old outbox rows ({ex.Message.Split('\n')[0]}) — the tests run anyway.");
        }

        // 2. The index on the address the tests look up.
        try
        {
            await using var conn = await OpenAsync();
            await using var idx = new SqlCommand(
                @"IF NOT EXISTS (SELECT 1 FROM sys.indexes
                                 WHERE name = 'IX_E2E_OutboundMessages_To'
                                   AND object_id = OBJECT_ID('dbo.OutboundMessages'))
                      CREATE INDEX IX_E2E_OutboundMessages_To
                          ON dbo.OutboundMessages (ToAddress, EventType, CreatedAtUtc);", conn)
            { CommandTimeout = 180 };
            await idx.ExecuteNonQueryAsync();
            TestContext.Progress.WriteLine("[e2e setup] Outbox: address index in place.");
        }
        catch (Exception ex)
        {
            TestContext.Progress.WriteLine(
                $"[e2e setup] Could not add the outbox address index ({ex.Message.Split('\n')[0]}) — the tests run anyway.");
        }
    }

    /// <summary>095. Removes the run-time index again — see PrepareOutboxAsync. Never throws.</summary>
    public async Task DropOutboxIndexAsync()
    {
        try
        {
            await EnsureIsE2EDatabaseAsync();
            await using var conn = await OpenAsync();
            await using var cmd = new SqlCommand(
                @"IF EXISTS (SELECT 1 FROM sys.indexes
                             WHERE name = 'IX_E2E_OutboundMessages_To'
                               AND object_id = OBJECT_ID('dbo.OutboundMessages'))
                      DROP INDEX IX_E2E_OutboundMessages_To ON dbo.OutboundMessages;", conn)
            { CommandTimeout = 60 };
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            TestContext.Progress.WriteLine(
                $"[e2e] Could not remove the outbox address index ({ex.Message.Split('\n')[0]}).");
        }
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

    // ── 094: the board on a phone (read-only) ─────────────────────────

    /// <summary>A deal's current stage key; null when there is no such deal.</summary>
    public async Task<string?> DealStageAsync(Guid dealId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand("SELECT Stage FROM Deals WHERE Id = @id", conn);
        cmd.Parameters.AddWithValue("@id", dealId);
        return await cmd.ExecuteScalarAsync() as string;
    }

    // ── 098: automation rules ─────────────────────────────────────────

    /// <summary>AutomationRunStatus values as stored.</summary>
    public const int RunDone = 0, RunSkipped = 1, RunFailed = 2, RunPartial = 3;

    /// <summary>
    /// Lines in the rule log for one record from rules whose name contains
    /// <paramref name="ruleNameContains"/>, with this result.
    /// </summary>
    public async Task<int> CountAutomationRunsAsync(Guid entityId, string ruleNameContains, int status)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT COUNT(*) FROM AutomationRuns WHERE EntityId = @e AND Status = @s AND CHARINDEX(@n, RuleName) > 0", conn);
        cmd.Parameters.AddWithValue("@e", entityId);
        cmd.Parameters.AddWithValue("@s", status);
        cmd.Parameters.AddWithValue("@n", ruleNameContains);
        return (int)(await cmd.ExecuteScalarAsync() ?? 0);
    }

    /// <summary>The workspace's plan and its live automation limit.</summary>
    public async Task<(Guid PlanId, int Limit)> PlanAutomationLimitAsync(Guid tenantId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            @"SELECT TOP 1 p.Id, p.MaxAutomationRules FROM Tenants t JOIN Plans p ON p.Name = t.[Plan] WHERE t.Id = @t", conn);
        cmd.Parameters.AddWithValue("@t", tenantId);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync())
            throw new InvalidOperationException("The workspace's plan was not found — has 098_Automation.sql been run on the test database?");
        return (r.GetGuid(0), r.GetInt32(1));
    }

    /// <summary>Puts a plan's automation limit back after a test changed it. _E2E only, like everything here.</summary>
    public async Task SetPlanAutomationLimitAsync(Guid planId, int limit)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand("UPDATE Plans SET MaxAutomationRules = @m WHERE Id = @id", conn);
        cmd.Parameters.AddWithValue("@m", limit);
        cmd.Parameters.AddWithValue("@id", planId);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Switched-on, live rules in a workspace.</summary>
    public async Task<int> CountActiveAutomationRulesAsync(Guid tenantId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT COUNT(*) FROM AutomationRules WHERE TenantId = @t AND IsActive = 1 AND IsDeleted = 0", conn);
        cmd.Parameters.AddWithValue("@t", tenantId);
        return (int)(await cmd.ExecuteScalarAsync() ?? 0);
    }

    /// <summary>
    /// Switches off and removes a test's rules, so they never fire on the
    /// leads and deals later tests create.
    /// </summary>
    public async Task RetireAutomationRulesAsync(Guid tenantId, string nameContains)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            "UPDATE AutomationRules SET IsActive = 0, IsDeleted = 1 WHERE TenantId = @t AND CHARINDEX(@n, Name) > 0", conn);
        cmd.Parameters.AddWithValue("@t", tenantId);
        cmd.Parameters.AddWithValue("@n", nameContains);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 099. Moves a lead's "added" time N days into the past. _E2E only.
    /// The lead must have no activity or notes of its own, or those would
    /// still count as recent.
    /// </summary>
    public async Task BackdateLeadAsync(Guid leadId, int days)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            "UPDATE Leads SET CreatedAtUtc = DATEADD(day, -@d, CreatedAtUtc) WHERE Id = @id", conn);
        cmd.Parameters.AddWithValue("@d", days);
        cmd.Parameters.AddWithValue("@id", leadId);
        await cmd.ExecuteNonQueryAsync();
    }

    // ── 100: identities for API tokens (read-only) ───────────────────

    /// <summary>
    /// A person as the API will see them: their workspace, whether they are
    /// its admin, its plan, and every "module.action" their roles grant —
    /// read exactly as SignInService.BuildPrincipalAsync reads it.
    /// </summary>
    public async Task<ApiIdentity> ApiIdentityAsync(string email)
    {
        await using var conn = await OpenAsync();

        Guid userId, tenantId;
        bool isAdmin, isSuper;
        string plan, storedEmail, stamp;
        await using (var cmd = new SqlCommand(
            @"SELECT TOP 1 u.Id, u.TenantId, u.Email, u.IsTenantAdmin, u.IsSuperAdmin, t.[Plan], u.SecurityStamp
              FROM Users u JOIN Tenants t ON t.Id = u.TenantId
              WHERE u.Email = @e AND u.IsDeleted = 0", conn))
        {
            cmd.Parameters.AddWithValue("@e", email);
            await using var r = await cmd.ExecuteReaderAsync();
            if (!await r.ReadAsync())
                throw new InvalidOperationException($"No live user with the email {email} in the test database.");
            userId = r.GetGuid(0);
            tenantId = r.GetGuid(1);
            storedEmail = r.GetString(2);
            isAdmin = r.GetBoolean(3);
            isSuper = r.GetBoolean(4);
            plan = r.IsDBNull(5) ? "starter" : r.GetString(5);
            stamp = r.IsDBNull(6) ? "" : r.GetString(6);     // 102 — as BuildPrincipalAsync: null becomes ""
        }

        var permissions = new List<string>();
        await using (var cmd = new SqlCommand(
            @"SELECT r.Permissions FROM UserRoles ur JOIN Roles r ON r.Id = ur.RoleId
              WHERE ur.UserId = @u AND r.IsDeleted = 0 AND (r.TenantId IS NULL OR r.TenantId = @t)", conn))
        {
            cmd.Parameters.AddWithValue("@u", userId);
            cmd.Parameters.AddWithValue("@t", tenantId);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
            {
                if (r.IsDBNull(0)) continue;
                var json = r.GetString(0);
                if (string.IsNullOrWhiteSpace(json)) continue;
                try
                {
                    var perms = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(
                        json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (perms is null) continue;
                    foreach (var (module, actions) in perms)
                        foreach (var action in actions)
                            permissions.Add($"{module}.{action}");
                }
                catch (JsonException)
                {
                    // SignInService grants nothing from a broken role either.
                }
            }
        }

        return new ApiIdentity(userId, tenantId, storedEmail, isAdmin, isSuper, plan, permissions, stamp);
    }

    // ── 100: records exactly as stored (read-only) ────────────────────

    private static readonly HashSet<string> SnapshotTables = new(StringComparer.Ordinal)
    {
        "Leads", "Contacts", "Companies", "Deals", "Quotes", "QuoteItems", "Invoices", "InvoiceLines",
        "Payments", "Products", "Activities", "LeadNotes", "DealNotes", "LeadActivities", "DealActivities",
        "LeadReminders", "DealReminders", "Attachments", "DealStageHistory", "QuoteApprovalRequests",
        // 104 — the configuration and people the VUL-05 suite tries to reach
        "Users", "UserRoles", "Roles", "LeadStatusDefinitions", "PipelineStages", "CustomFieldDefinitions",
        "ProductCategories", "AutomationRules", "Teams", "Notifications", "OutboundMessages", "QuoteMilestones",
    };

    private static readonly HashSet<string> SnapshotColumns = new(StringComparer.Ordinal)
    {
        "Id", "LeadId", "DealId", "QuoteId", "InvoiceId", "ContactId", "CompanyId", "EntityId", "TenantId",
        "ProductId",   // 100b — quote and invoice lines that name a product
        "UserId",      // 104 — a person's roles
    };

    /// <summary>
    /// One row, every column, as JSON — or "(no row)". Two reads that
    /// differ mean something changed it.
    /// </summary>
    public async Task<string> RowJsonAsync(string table, Guid id)
    {
        if (!SnapshotTables.Contains(table)) throw new ArgumentOutOfRangeException(nameof(table), table, "Not a snapshot table.");
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand($"SELECT * FROM dbo.[{table}] WHERE Id = @id FOR JSON PATH, INCLUDE_NULL_VALUES", conn);
        cmd.Parameters.AddWithValue("@id", id);

        // FOR JSON can come back split over several rows.
        var sb = new System.Text.StringBuilder();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) sb.Append(r.GetString(0));
        return sb.Length == 0 ? "(no row)" : sb.ToString();
    }

    /// <summary>Rows in <paramref name="table"/> whose <paramref name="column"/> is <paramref name="value"/>, deleted ones included.</summary>
    public async Task<int> CountWhereAsync(string table, string column, Guid value)
    {
        if (!SnapshotTables.Contains(table)) throw new ArgumentOutOfRangeException(nameof(table), table, "Not a snapshot table.");
        if (!SnapshotColumns.Contains(column)) throw new ArgumentOutOfRangeException(nameof(column), column, "Not a snapshot column.");
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand($"SELECT COUNT(*) FROM dbo.[{table}] WHERE [{column}] = @v", conn);
        cmd.Parameters.AddWithValue("@v", value);
        return (int)(await cmd.ExecuteScalarAsync() ?? 0);
    }

    /// <summary>A live attachment's id on a record, by its file name.</summary>
    public async Task<Guid?> AttachmentIdAsync(Guid entityId, string fileName)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT TOP 1 Id FROM Attachments WHERE EntityId = @e AND FileName = @f AND IsDeleted = 0", conn);
        cmd.Parameters.AddWithValue("@e", entityId);
        cmd.Parameters.AddWithValue("@f", fileName);
        return await cmd.ExecuteScalarAsync() is Guid g ? g : null;
    }

    /// <summary>The newest live row in a notes/activities table for a record whose text column contains the marker.</summary>
    public async Task<Guid?> ChildIdAsync(string table, string parentColumn, Guid parentId, string textColumn, string contains)
    {
        if (!SnapshotTables.Contains(table) || !SnapshotColumns.Contains(parentColumn))
            throw new ArgumentOutOfRangeException(nameof(table));
        if (textColumn is not ("Note" or "Subject" or "Description" or "Title"))
            throw new ArgumentOutOfRangeException(nameof(textColumn));
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            $"SELECT TOP 1 Id FROM dbo.[{table}] WHERE [{parentColumn}] = @p AND CHARINDEX(@c, [{textColumn}]) > 0 ORDER BY CreatedAtUtc DESC", conn);
        cmd.Parameters.AddWithValue("@p", parentId);
        cmd.Parameters.AddWithValue("@c", contains);
        return await cmd.ExecuteScalarAsync() is Guid g ? g : null;
    }

    /// <summary>A user's id from their email.</summary>
    public async Task<Guid> UserIdAsync(string email)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand("SELECT TOP 1 Id FROM Users WHERE Email = @e AND IsDeleted = 0", conn);
        cmd.Parameters.AddWithValue("@e", email);
        return await cmd.ExecuteScalarAsync() is Guid g ? g : throw new InvalidOperationException($"No user {email}.");
    }

    // ── 100: one conversion (read-only) ───────────────────────────────

    public sealed record LeadConversion(bool IsConverted, Guid? DealId, Guid? ContactId, Guid? ConvertedToContactId, string? Status);

    public async Task<LeadConversion> LeadConversionAsync(Guid leadId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT IsConverted, DealId, ContactId, ConvertedToContactId, Status FROM Leads WHERE Id = @id", conn);
        cmd.Parameters.AddWithValue("@id", leadId);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) throw new InvalidOperationException($"No lead {leadId}.");
        return new LeadConversion(
            r.GetBoolean(0),
            r.IsDBNull(1) ? null : r.GetGuid(1),
            r.IsDBNull(2) ? null : r.GetGuid(2),
            r.IsDBNull(3) ? null : r.GetGuid(3),
            r.IsDBNull(4) ? null : Convert.ToString(r.GetValue(4)));
    }

    /// <summary>Live deals made from this lead.</summary>
    public async Task<List<Guid>> DealIdsForLeadAsync(Guid leadId)
    {
        var ids = new List<Guid>();
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand("SELECT Id FROM Deals WHERE LeadId = @l AND IsDeleted = 0", conn);
        cmd.Parameters.AddWithValue("@l", leadId);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) ids.Add(r.GetGuid(0));
        return ids;
    }

    /// <summary>A lead's status key as stored.</summary>
    public async Task<string?> LeadStatusAsync(Guid leadId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand("SELECT Status FROM Leads WHERE Id = @id", conn);
        cmd.Parameters.AddWithValue("@id", leadId);
        return Convert.ToString(await cmd.ExecuteScalarAsync());
    }

    // ── 100: payments (read-only) ─────────────────────────────────────

    public sealed record StoredPayment(Guid Id, decimal Amount, string Status, Guid? IdempotencyKey);

    public async Task<List<StoredPayment>> PaymentsAsync(Guid invoiceId)
    {
        var list = new List<StoredPayment>();
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT Id, Amount, Status, IdempotencyKey FROM Payments WHERE InvoiceId = @i AND IsDeleted = 0 ORDER BY CreatedAtUtc", conn);
        cmd.Parameters.AddWithValue("@i", invoiceId);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new StoredPayment(r.GetGuid(0), r.GetDecimal(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetGuid(3)));
        return list;
    }

    public sealed record StoredInvoice(string Status, decimal Total, decimal Balance);

    /// <summary>An invoice's status (as stored — a name or a number), total and balance.</summary>
    public async Task<StoredInvoice> InvoiceAsync(Guid invoiceId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand("SELECT Status, Total, Balance FROM Invoices WHERE Id = @id", conn);
        cmd.Parameters.AddWithValue("@id", invoiceId);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) throw new InvalidOperationException($"No invoice {invoiceId}.");
        return new StoredInvoice(Convert.ToString(r.GetValue(0)) ?? "", r.GetDecimal(1), r.GetDecimal(2));
    }

    /// <summary>Live invoices raised from a quote.</summary>
    public async Task<int> CountInvoicesForQuoteAsync(Guid quoteId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand("SELECT COUNT(*) FROM Invoices WHERE QuoteId = @q AND IsDeleted = 0", conn);
        cmd.Parameters.AddWithValue("@q", quoteId);
        return (int)(await cmd.ExecuteScalarAsync() ?? 0);
    }

    // ── 100: quotes (read-only) ───────────────────────────────────────

    public sealed record StoredQuote(string Status, decimal GrandTotal, decimal LinesGrandTotal, decimal MaxDiscountPercent, string? PublicLinkToken);

    /// <summary>
    /// A quote's status, its stored grand total, the grand total its live
    /// lines add up to, and the biggest discount on any line, as a
    /// percentage of that line's gross.
    /// </summary>
    public async Task<StoredQuote> QuoteAsync(Guid quoteId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            @"SELECT q.Status, q.GrandTotal, q.PublicLinkToken,
                     ISNULL((SELECT SUM(CASE WHEN i.UnitPrice * i.Quantity - i.LineDiscount < 0 THEN 0
                                             ELSE i.UnitPrice * i.Quantity - i.LineDiscount END * (1 + i.TaxRate))
                             FROM QuoteItems i WHERE i.QuoteId = q.Id AND i.IsDeleted = 0), 0),
                     ISNULL((SELECT MAX(CASE WHEN i.UnitPrice * i.Quantity > 0
                                             THEN 100.0 * i.LineDiscount / (i.UnitPrice * i.Quantity) ELSE 0 END)
                             FROM QuoteItems i WHERE i.QuoteId = q.Id AND i.IsDeleted = 0), 0)
              FROM Quotes q WHERE q.Id = @id", conn);
        cmd.Parameters.AddWithValue("@id", quoteId);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) throw new InvalidOperationException($"No quote {quoteId}.");
        return new StoredQuote(
            Convert.ToString(r.GetValue(0)) ?? "",
            r.GetDecimal(1),
            Convert.ToDecimal(r.GetValue(3)),
            Convert.ToDecimal(r.GetValue(4)),
            r.IsDBNull(2) ? null : r.GetString(2));
    }

    // ── 100: automation (read-only, plus one tidy-up) ─────────────────

    /// <summary>"Already acted on" rows for one record from rules whose name contains the text.</summary>
    public async Task<int> CountAutomationTimeFiresAsync(Guid entityId, string ruleNameContains)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            @"SELECT COUNT(*) FROM AutomationTimeFires f JOIN AutomationRules r ON r.Id = f.RuleId
              WHERE f.EntityId = @e AND CHARINDEX(@n, r.Name) > 0", conn);
        cmd.Parameters.AddWithValue("@e", entityId);
        cmd.Parameters.AddWithValue("@n", ruleNameContains);
        return (int)(await cmd.ExecuteScalarAsync() ?? 0);
    }

    /// <summary>Every rule-log line for one record from rules whose name contains the text, whatever its result.</summary>
    public async Task<int> CountAllAutomationRunsAsync(Guid entityId, string ruleNameContains)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT COUNT(*) FROM AutomationRuns WHERE EntityId = @e AND CHARINDEX(@n, RuleName) > 0", conn);
        cmd.Parameters.AddWithValue("@e", entityId);
        cmd.Parameters.AddWithValue("@n", ruleNameContains);
        return (int)(await cmd.ExecuteScalarAsync() ?? 0);
    }

    /// <summary>
    /// Removes one rule's log lines for one record — "the worker stopped
    /// after acting but before its log line was kept". _E2E only, like
    /// everything here. The task and the "acted on" row stay.
    /// </summary>
    public async Task DeleteAutomationRunsAsync(Guid entityId, string ruleNameContains)
    {
        await EnsureIsE2EDatabaseAsync();
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            "DELETE FROM AutomationRuns WHERE EntityId = @e AND CHARINDEX(@n, RuleName) > 0", conn);
        cmd.Parameters.AddWithValue("@e", entityId);
        cmd.Parameters.AddWithValue("@n", ruleNameContains);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Live tasks (and activities) on a record whose subject contains the text, done or not.</summary>
    public async Task<int> CountTasksAsync(Guid entityId, string subjectContains)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT COUNT(*) FROM Activities WHERE EntityId = @e AND IsDeleted = 0 AND CHARINDEX(@s, Subject) > 0", conn);
        cmd.Parameters.AddWithValue("@e", entityId);
        cmd.Parameters.AddWithValue("@s", subjectContains);
        return (int)(await cmd.ExecuteScalarAsync() ?? 0);
    }

    // ── 101: roles (read-only) ────────────────────────────────────────

    /// <summary>The ids of every role a person holds, so a test that adds one can put them back.</summary>
    public async Task<List<Guid>> UserRoleIdsAsync(string email)
    {
        var ids = new List<Guid>();
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            @"SELECT ur.RoleId FROM UserRoles ur JOIN Users u ON u.Id = ur.UserId
              WHERE u.Email = @e AND u.IsDeleted = 0", conn);
        cmd.Parameters.AddWithValue("@e", email);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) ids.Add(r.GetGuid(0));
        return ids;
    }

    /// <summary>
    /// 101. The biggest discount on any live line of an invoice, as a
    /// percentage of that line's gross — what the customer would get.
    /// </summary>
    public async Task<decimal> InvoiceMaxDiscountPercentAsync(Guid invoiceId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            @"SELECT ISNULL(MAX(CASE WHEN UnitPrice * Quantity > 0
                                     THEN 100.0 * LineDiscount / (UnitPrice * Quantity) ELSE 0 END), 0)
              FROM InvoiceLines WHERE InvoiceId = @i AND IsDeleted = 0", conn);
        cmd.Parameters.AddWithValue("@i", invoiceId);
        return Convert.ToDecimal(await cmd.ExecuteScalarAsync());
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

    // ── 102: read-backs for tests 83–90 (read-only, plus one plan-limit setter) ──

    /// <summary>Live leads in the workspace whose name, email or company contains <paramref name="marker"/>.</summary>
    public async Task<List<StoredLead>> LeadsMatchingAsync(Guid tenantId, string marker)
    {
        var list = new List<StoredLead>();
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            @"SELECT Id, FullName, Email, Phone, CompanyName, Source FROM Leads
              WHERE TenantId = @t AND IsDeleted = 0
                AND (CHARINDEX(@m, FullName) > 0 OR CHARINDEX(@m, ISNULL(Email, '')) > 0 OR CHARINDEX(@m, ISNULL(CompanyName, '')) > 0)
              ORDER BY CreatedAtUtc", conn);
        cmd.Parameters.AddWithValue("@t", tenantId);
        cmd.Parameters.AddWithValue("@m", marker);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new StoredLead(r.GetGuid(0), r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5)));
        return list;
    }

    public sealed record StoredLead(Guid Id, string FullName, string? Email, string? Phone, string? CompanyName, string? Source);

    /// <summary>One lead as stored now.</summary>
    public async Task<StoredLead> LeadAsync(Guid leadId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT Id, FullName, Email, Phone, CompanyName, Source FROM Leads WHERE Id = @id", conn);
        cmd.Parameters.AddWithValue("@id", leadId);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) throw new InvalidOperationException($"No lead {leadId}.");
        return new StoredLead(r.GetGuid(0), r.GetString(1),
            r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
            r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5));
    }

    /// <summary>Live leads in the workspace.</summary>
    public async Task<int> CountLiveLeadsAsync(Guid tenantId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand("SELECT COUNT(*) FROM Leads WHERE TenantId = @t AND IsDeleted = 0", conn);
        cmd.Parameters.AddWithValue("@t", tenantId);
        return (int)(await cmd.ExecuteScalarAsync() ?? 0);
    }

    /// <summary>The workspace's lead limit (TenantSettings.MaxLeads), or null with no settings row.</summary>
    public async Task<int?> MaxLeadsAsync(Guid tenantId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand("SELECT TOP 1 MaxLeads FROM TenantSettings WHERE TenantId = @t", conn);
        cmd.Parameters.AddWithValue("@t", tenantId);
        return await cmd.ExecuteScalarAsync() is int n ? n : null;
    }

    /// <summary>
    /// Sets the workspace's lead limit. Test 86 lowers it for one step to
    /// prove a refused import writes nothing, and ALWAYS puts it back.
    /// </summary>
    public async Task SetMaxLeadsAsync(Guid tenantId, int maxLeads)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand("UPDATE TenantSettings SET MaxLeads = @m WHERE TenantId = @t", conn);
        cmd.Parameters.AddWithValue("@m", maxLeads);
        cmd.Parameters.AddWithValue("@t", tenantId);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Is the row soft-deleted? Null when there is no such row.</summary>
    public async Task<bool?> IsRowDeletedAsync(string table, Guid id)
    {
        if (table is not ("Deals" or "Contacts" or "Companies" or "Quotes" or "Leads" or "Invoices"))
            throw new ArgumentOutOfRangeException(nameof(table), table, "Not a soft-delete table.");
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand($"SELECT IsDeleted FROM dbo.[{table}] WHERE Id = @id", conn);
        cmd.Parameters.AddWithValue("@id", id);
        return await cmd.ExecuteScalarAsync() is bool b ? b : null;
    }

    /// <summary>Audit rows for one record with this Action ("DealDeleted", "DealStageChanged"…).</summary>
    public async Task<int> CountAuditAsync(Guid entityId, string action)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand("SELECT COUNT(*) FROM AuditLogs WHERE EntityId = @e AND [Action] = @a", conn);
        cmd.Parameters.AddWithValue("@e", entityId);
        cmd.Parameters.AddWithValue("@a", action);
        return (int)(await cmd.ExecuteScalarAsync() ?? 0);
    }

    public sealed record StageMove(string? From, string To, string? By, DateTime AtUtc);

    /// <summary>A deal's stage history, oldest first.</summary>
    public async Task<List<StageMove>> StageHistoryAsync(Guid dealId)
    {
        var list = new List<StageMove>();
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT FromStage, ToStage, ChangedBy, ChangedAtUtc FROM DealStageHistory WHERE DealId = @d ORDER BY ChangedAtUtc, Id", conn);
        cmd.Parameters.AddWithValue("@d", dealId);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new StageMove(r.IsDBNull(0) ? null : r.GetString(0), r.GetString(1),
                r.IsDBNull(2) ? null : r.GetString(2), r.GetDateTime(3)));
        return list;
    }

    /// <summary>One text column of one row, as stored ("Deals"/"Title", "Companies"/"Name"…).</summary>
    public async Task<string?> TextAsync(string table, string column, Guid id)
    {
        var allowed = (table, column) switch
        {
            ("Deals", "Title" or "Description" or "Tags") => true,
            ("Contacts", "FirstName" or "LastName" or "JobTitle" or "Phone" or "Email") => true,
            ("Companies", "Name" or "TaxId") => true,
            ("Leads", "FullName" or "CompanyName" or "Phone" or "Email" or "Address") => true,
            _ => false,
        };
        if (!allowed) throw new ArgumentOutOfRangeException(nameof(column), $"{table}.{column}");
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand($"SELECT [{column}] FROM dbo.[{table}] WHERE Id = @id", conn);
        cmd.Parameters.AddWithValue("@id", id);
        var v = await cmd.ExecuteScalarAsync();
        return v is null or DBNull ? null : Convert.ToString(v);
    }

    /// <summary>Live quote lines (name, quantity, unit price), in order — to prove an accepted quote's lines never move.</summary>
    public async Task<string> QuoteLinesAsync(Guid quoteId)
    {
        var parts = new List<string>();
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT Name, Quantity, UnitPrice, LineDiscount, TaxRate FROM QuoteItems WHERE QuoteId = @q AND IsDeleted = 0 ORDER BY CreatedAtUtc, Id", conn);
        cmd.Parameters.AddWithValue("@q", quoteId);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            parts.Add($"{r.GetString(0)} | {r.GetValue(1)} × {r.GetValue(2)} − {r.GetValue(3)} @ {r.GetValue(4)}");
        return string.Join(" ; ", parts);
    }

    // ── 103: time zones (test 91) and plan limits / trial state (test 92) ──

    /// <summary>The workspace's own time zone id as stored (Tenants.Timezone).</summary>
    public async Task<string?> TenantTimezoneAsync(Guid tenantId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand("SELECT Timezone FROM Tenants WHERE Id = @t", conn);
        cmd.Parameters.AddWithValue("@t", tenantId);
        var v = await cmd.ExecuteScalarAsync();
        return v is null or DBNull ? null : (string)v;
    }

    /// <summary>The stored (UTC) due time of each live task on a record whose subject contains the text, oldest first.</summary>
    public async Task<List<DateTime>> TaskDueTimesAsync(Guid entityId, string subjectContains)
    {
        var list = new List<DateTime>();
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            @"SELECT DueDate FROM Activities
              WHERE EntityId = @e AND IsDeleted = 0 AND IsTask = 1 AND DueDate IS NOT NULL AND CHARINDEX(@s, Subject) > 0
              ORDER BY CreatedAtUtc, Id", conn);
        cmd.Parameters.AddWithValue("@e", entityId);
        cmd.Parameters.AddWithValue("@s", subjectContains);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(DateTime.SpecifyKind(r.GetDateTime(0), DateTimeKind.Utc));
        return list;
    }

    public sealed record WorkspaceLimits(int MaxUsers, int MaxLeads, int MaxDeals);

    /// <summary>The workspace's own limits (TenantSettings), or null with no settings row.</summary>
    public async Task<WorkspaceLimits?> WorkspaceLimitsAsync(Guid tenantId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT TOP 1 MaxUsers, MaxLeads, MaxDeals FROM TenantSettings WHERE TenantId = @t", conn);
        cmd.Parameters.AddWithValue("@t", tenantId);
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? new WorkspaceLimits(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2)) : null;
    }

    /// <summary>Puts a workspace's limits back after a test changed them. _E2E only, like everything here.</summary>
    public async Task SetWorkspaceLimitsAsync(Guid tenantId, WorkspaceLimits limits)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            "UPDATE TenantSettings SET MaxUsers = @u, MaxLeads = @l, MaxDeals = @d WHERE TenantId = @t", conn);
        cmd.Parameters.AddWithValue("@u", limits.MaxUsers);
        cmd.Parameters.AddWithValue("@l", limits.MaxLeads);
        cmd.Parameters.AddWithValue("@d", limits.MaxDeals);
        cmd.Parameters.AddWithValue("@t", tenantId);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Live (not deleted) rows of a workspace in one of the tables a plan limits.</summary>
    public async Task<int> CountLiveAsync(string table, Guid tenantId)
    {
        if (table is not ("Leads" or "Deals" or "Contacts" or "Companies" or "Users"))
            throw new ArgumentOutOfRangeException(nameof(table), table, "Not a table a plan limits.");
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand($"SELECT COUNT(*) FROM dbo.[{table}] WHERE TenantId = @t AND IsDeleted = 0", conn);
        cmd.Parameters.AddWithValue("@t", tenantId);
        return (int)(await cmd.ExecuteScalarAsync() ?? 0);
    }

    public sealed record TrialState(string Plan, DateTime? TrialExpiresAt, bool IsSuspended);

    /// <summary>The workspace's plan name, trial end and suspended switch, as stored.</summary>
    public async Task<TrialState> TrialStateAsync(Guid tenantId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand("SELECT [Plan], TrialExpiresAt, IsSuspended FROM Tenants WHERE Id = @t", conn);
        cmd.Parameters.AddWithValue("@t", tenantId);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) throw new InvalidOperationException($"No workspace {tenantId}.");
        return new TrialState(r.GetString(0), r.IsDBNull(1) ? null : DateTime.SpecifyKind(r.GetDateTime(1), DateTimeKind.Utc), r.GetBoolean(2));
    }

    /// <summary>
    /// 103. Moves a workspace's trial clock (or its suspended switch). There
    /// is no screen that ends a trial early — time does — so the test moves
    /// the end date instead, and ALWAYS puts the original back. _E2E only.
    /// </summary>
    public async Task SetTrialStateAsync(Guid tenantId, TrialState state)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            "UPDATE Tenants SET [Plan] = @p, TrialExpiresAt = @x, IsSuspended = @s WHERE Id = @t", conn);
        cmd.Parameters.AddWithValue("@p", state.Plan);
        cmd.Parameters.AddWithValue("@x", (object?)state.TrialExpiresAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@s", state.IsSuspended);
        cmd.Parameters.AddWithValue("@t", tenantId);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>A lead's stored status key and whether it is converted.</summary>
    public async Task<(string Status, bool IsConverted)> LeadStateAsync(Guid leadId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand("SELECT Status, IsConverted FROM Leads WHERE Id = @id", conn);
        cmd.Parameters.AddWithValue("@id", leadId);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) throw new InvalidOperationException($"No lead {leadId}.");
        return (r.GetString(0), r.GetBoolean(1));
    }

    /// <summary>Is there a live account with this email (in any workspace)?</summary>
    public async Task<bool> LiveUserExistsAsync(string email)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand("SELECT COUNT(*) FROM Users WHERE Email = @e AND IsDeleted = 0", conn);
        cmd.Parameters.AddWithValue("@e", email);
        return (int)(await cmd.ExecuteScalarAsync() ?? 0) > 0;
    }

    // ── 104: security tests (VUL-01 … VUL-10) ─────────────────────────

    private static readonly HashSet<string> FirstIdTables = new(StringComparer.Ordinal)
    {
        "Roles", "LeadStatusDefinitions", "PipelineStages", "Notifications", "OutboundMessages", "Quotes", "LeadSources",
    };

    /// <summary>
    /// One live row of a workspace's own in a table (the oldest by id), or
    /// null when it has none — so a test can aim at a record of that kind.
    /// </summary>
    public async Task<Guid?> FirstIdAsync(string table, Guid tenantId)
    {
        if (!FirstIdTables.Contains(table)) throw new ArgumentOutOfRangeException(nameof(table), table, "Not a table this helper reads.");
        // Lead statuses, pipeline stages, notifications and the outbox have no IsDeleted column.
        var live = table is "Notifications" or "OutboundMessages" or "LeadStatusDefinitions" or "PipelineStages" ? "" : " AND IsDeleted = 0";
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand($"SELECT TOP 1 Id FROM dbo.[{table}] WHERE TenantId = @t{live} ORDER BY Id", conn);
        cmd.Parameters.AddWithValue("@t", tenantId);
        return await cmd.ExecuteScalarAsync() is Guid g ? g : null;
    }

    private static readonly HashSet<(string Table, string Column)> ValueColumns = new()
    {
        ("Leads", "TenantId"), ("Leads", "OwnerUserId"), ("Leads", "Status"), ("Leads", "IsConverted"), ("Leads", "Score"),
        ("Leads", "CreatedBy"), ("Leads", "CreatedAtUtc"), ("Leads", "IsDeleted"), ("Leads", "SourceId"), ("Leads", "FullName"),
        ("Contacts", "TenantId"), ("Companies", "TenantId"), ("Deals", "TenantId"), ("Deals", "Stage"), ("Deals", "CreatedBy"),
        ("Deals", "OwnerUserId"), ("Deals", "ActualValue"),
        ("Quotes", "Status"), ("Quotes", "TenantId"), ("Quotes", "Number"), ("Quotes", "GrandTotal"),
        ("Invoices", "Status"), ("Invoices", "Balance"), ("Invoices", "Number"), ("Invoices", "TenantId"),
        ("Users", "IsTenantAdmin"), ("Users", "IsSuperAdmin"), ("Users", "Email"), ("Users", "IsActive"), ("Users", "TenantId"),
        ("Users", "MustChangePassword"), ("Users", "FirstName"),
        ("Roles", "Permissions"), ("Activities", "AssignedToUserId"), ("Activities", "TenantId"),
        // 106
        ("Quotes", "PublicLinkToken"), ("Invoices", "PublicLinkToken"),
        ("Users", "LockoutEndUtc"), ("Users", "FailedLoginCount"), ("Users", "SecurityStamp"),
    };

    /// <summary>One column of one row, as text ("" for NULL, null for no such row). Only the pairs above.</summary>
    public async Task<string?> ValueAsync(string table, string column, Guid id)
    {
        if (!ValueColumns.Contains((table, column))) throw new ArgumentOutOfRangeException(nameof(column), $"{table}.{column}");
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand($"SELECT [{column}] FROM dbo.[{table}] WHERE Id = @id", conn);
        cmd.Parameters.AddWithValue("@id", id);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;
        if (r.IsDBNull(0)) return "";
        var v = r.GetValue(0);
        return v switch
        {
            DateTime d => d.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
            _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    /// <summary>Live rows of a workspace whose name/title text contains the marker (Leads, Deals, Contacts, Companies).</summary>
    public async Task<int> CountContainingAsync(string table, Guid tenantId, string marker)
    {
        var column = table switch
        {
            "Leads" => "FullName", "Deals" => "Title", "Contacts" => "FirstName", "Companies" => "Name",
            _ => throw new ArgumentOutOfRangeException(nameof(table), table, "Not a table this helper counts."),
        };
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            $"SELECT COUNT(*) FROM dbo.[{table}] WHERE TenantId = @t AND IsDeleted = 0 AND CHARINDEX(@m, ISNULL([{column}], '')) > 0", conn);
        cmd.Parameters.AddWithValue("@t", tenantId);
        cmd.Parameters.AddWithValue("@m", marker);
        return (int)(await cmd.ExecuteScalarAsync() ?? 0);
    }

    /// <summary>Every row in a table, deleted ones included — for "nothing was dropped or wiped" checks.</summary>
    public async Task<int> CountAllRowsAsync(string table)
    {
        if (table is not ("Leads" or "Deals" or "Contacts" or "Companies" or "Users" or "Tenants" or "Quotes" or "Invoices" or "Products"))
            throw new ArgumentOutOfRangeException(nameof(table), table, "Not a table this helper counts.");
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand($"SELECT COUNT(*) FROM dbo.[{table}]", conn);
        return (int)(await cmd.ExecuteScalarAsync() ?? 0);
    }

    /// <summary>Reset links issued to a person since a moment — what the "3 an hour" rule counts.</summary>
    public async Task<int> CountResetLinksAsync(Guid userId, DateTime sinceUtc)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT COUNT(*) FROM UserTokens WHERE UserId = @u AND Purpose = 'PasswordReset' AND CreatedAtUtc >= @s", conn);
        cmd.Parameters.AddWithValue("@u", userId);
        cmd.Parameters.AddWithValue("@s", sinceUtc);
        return (int)(await cmd.ExecuteScalarAsync() ?? 0);
    }

    public sealed record StoredLink(DateTime CreatedAtUtc, DateTime ExpiresAtUtc, DateTime? ConsumedAtUtc);

    /// <summary>The stored record of an emailed link (found by its hash, as the app finds it), or null.</summary>
    public async Task<StoredLink?> LinkAsync(string link)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT CreatedAtUtc, ExpiresAtUtc, ConsumedAtUtc FROM UserTokens WHERE TokenHash = @h", conn);
        cmd.Parameters.Add("@h", System.Data.SqlDbType.VarBinary, 64).Value = TokenHashOf(link);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;
        return new StoredLink(r.GetDateTime(0), r.GetDateTime(1), r.IsDBNull(2) ? null : r.GetDateTime(2));
    }

    /// <summary>
    /// 104 (VUL-09). Moves an emailed link's expiry a minute into the past —
    /// "two hours later", without waiting two hours. _E2E only.
    /// </summary>
    public async Task ExpireLinkAsync(string link)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            "UPDATE UserTokens SET ExpiresAtUtc = DATEADD(minute, -1, SYSUTCDATETIME()) WHERE TokenHash = @h", conn);
        cmd.Parameters.Add("@h", System.Data.SqlDbType.VarBinary, 64).Value = TokenHashOf(link);
        if (await cmd.ExecuteNonQueryAsync() != 1)
            throw new InvalidOperationException("The link's stored record was not found, so it could not be expired.");
    }

    /// <summary>The SHA-256 the app stores for a link's token (UserTokenService.HashToken).</summary>
    public static byte[] TokenHashOf(string link)
    {
        var m = Regex.Match(link, @"[?&]token=([^&#]+)", RegexOptions.IgnoreCase);
        if (!m.Success) throw new ArgumentException("No token in the link: " + link);
        var raw = Uri.UnescapeDataString(m.Groups[1].Value);
        return System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw));
    }

    // =================================================================
    // 105 — VUL-11 … VUL-20
    // =================================================================

    public sealed record StoredSession(Guid Id, DateTime CreatedAtUtc, DateTime ExpiresAtUtc, DateTime? EndedAtUtc, string? EndReason);

    /// <summary>105 (VUL-11). A person's sign-in sessions, oldest first.</summary>
    public async Task<List<StoredSession>> SessionsAsync(Guid userId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT Id, CreatedAtUtc, ExpiresAtUtc, EndedAtUtc, EndReason FROM dbo.UserSessions WHERE UserId = @u ORDER BY CreatedAtUtc", conn);
        cmd.Parameters.AddWithValue("@u", userId);
        var list = new List<StoredSession>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new StoredSession(r.GetGuid(0), r.GetDateTime(1), r.GetDateTime(2),
                r.IsDBNull(3) ? null : r.GetDateTime(3), r.IsDBNull(4) ? null : r.GetString(4)));
        return list;
    }

    /// <summary>105 (VUL-11). Moves a session's 24-hour limit a minute into the past — "a day later". _E2E only.</summary>
    public async Task ExpireSessionAsync(Guid sessionId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            "UPDATE dbo.UserSessions SET ExpiresAtUtc = DATEADD(minute, -1, SYSUTCDATETIME()) WHERE Id = @id", conn);
        cmd.Parameters.AddWithValue("@id", sessionId);
        if (await cmd.ExecuteNonQueryAsync() != 1) throw new InvalidOperationException($"No session {sessionId} to expire.");
    }

    public sealed record StoredAttachment(Guid Id, string FileName, string? MimeType, string FileUrl, long FileSize);

    /// <summary>105 (VUL-13/14). The live attachment of a record with this exact name, or null.</summary>
    public async Task<StoredAttachment?> AttachmentAsync(Guid entityId, string fileName)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT TOP 1 Id, FileName, MimeType, FileUrl, FileSize FROM dbo.Attachments " +
            "WHERE EntityId = @e AND FileName = @n AND IsDeleted = 0 ORDER BY CreatedAtUtc DESC", conn);
        cmd.Parameters.AddWithValue("@e", entityId);
        cmd.Parameters.AddWithValue("@n", fileName);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;
        return new StoredAttachment(r.GetGuid(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), Convert.ToInt64(r.GetValue(4)));
    }

    /// <summary>105 (VUL-13). Live attachments on a record.</summary>
    public async Task<int> CountAttachmentsAsync(Guid entityId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand("SELECT COUNT(*) FROM dbo.Attachments WHERE EntityId = @e AND IsDeleted = 0", conn);
        cmd.Parameters.AddWithValue("@e", entityId);
        return (int)(await cmd.ExecuteScalarAsync() ?? 0);
    }

    /// <summary>105 (VUL-14). An attachment's stored path, as the app wrote it.</summary>
    public async Task<string> AttachmentFileUrlAsync(Guid attachmentId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand("SELECT FileUrl FROM dbo.Attachments WHERE Id = @id", conn);
        cmd.Parameters.AddWithValue("@id", attachmentId);
        return await cmd.ExecuteScalarAsync() as string
            ?? throw new InvalidOperationException($"No attachment {attachmentId} in the test database.");
    }

    /// <summary>
    /// 105 (VUL-14). Points an attachment's row at another path — what a
    /// badly written row would look like. _E2E only; the test puts it back.
    /// </summary>
    public async Task SetAttachmentFileUrlAsync(Guid attachmentId, string fileUrl)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand("UPDATE dbo.Attachments SET FileUrl = @u WHERE Id = @id", conn);
        cmd.Parameters.AddWithValue("@id", attachmentId);
        cmd.Parameters.AddWithValue("@u", fileUrl);
        if (await cmd.ExecuteNonQueryAsync() != 1) throw new InvalidOperationException($"No attachment {attachmentId} to change.");
    }

    private static readonly HashSet<string> LeadTextColumns = new(StringComparer.Ordinal) { "FullName", "CompanyName", "Address", "Phone" };

    /// <summary>
    /// 105 (VUL-15). Writes a lead's text straight into the database, past
    /// every check — whatever got in (an import, an old version), the
    /// export must still not run it. _E2E only.
    /// </summary>
    public async Task SetLeadTextAsync(Guid leadId, string column, string value)
    {
        if (!LeadTextColumns.Contains(column)) throw new ArgumentOutOfRangeException(nameof(column), column, "Not a column this helper writes.");
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand($"UPDATE dbo.Leads SET [{column}] = @v WHERE Id = @id", conn);
        cmd.Parameters.AddWithValue("@id", leadId);
        cmd.Parameters.AddWithValue("@v", value);
        if (await cmd.ExecuteNonQueryAsync() != 1) throw new InvalidOperationException($"No lead {leadId} to change.");
    }

    /// <summary>105 (VUL-15). A person's first name, written past every check. _E2E only.</summary>
    public async Task SetUserFirstNameAsync(Guid userId, string firstName)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand("UPDATE dbo.Users SET FirstName = @v WHERE Id = @id", conn);
        cmd.Parameters.AddWithValue("@id", userId);
        cmd.Parameters.AddWithValue("@v", firstName);
        if (await cmd.ExecuteNonQueryAsync() != 1) throw new InvalidOperationException($"No user {userId} to change.");
    }

    /// <summary>105 (VUL-19). The newest email queued to an address, or null.</summary>
    public async Task<Guid?> LatestOutboundIdAsync(string toAddress)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT TOP 1 Id FROM dbo.OutboundMessages WHERE ToAddress = @a ORDER BY CreatedAtUtc DESC", conn);
        cmd.Parameters.AddWithValue("@a", toAddress);
        return await cmd.ExecuteScalarAsync() is Guid g ? g : null;
    }

    /// <summary>
    /// 105 (VUL-19). Makes a queued email look sent — the provider's id set,
    /// nothing heard back yet — so delivery events can be aimed at it.
    /// The test servers never send anything, so this is the only way one
    /// gets a provider id. _E2E only.
    /// </summary>
    public async Task PrepareOutboundForWebhookAsync(Guid messageId, string providerMessageId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            "UPDATE dbo.OutboundMessages SET ProviderMessageId = @p, DeliveryStatus = 0, DeliveryStatusAtUtc = NULL, DeliveryDetail = NULL " +
            "WHERE Id = @id", conn);
        cmd.Parameters.AddWithValue("@id", messageId);
        cmd.Parameters.AddWithValue("@p", providerMessageId);
        if (await cmd.ExecuteNonQueryAsync() != 1) throw new InvalidOperationException($"No queued email {messageId} to prepare.");
    }

    /// <summary>105 (VUL-19). What delivery events have recorded on an email: the status number and when.</summary>
    public async Task<(int Status, DateTime? AtUtc)> OutboundDeliveryAsync(Guid messageId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT DeliveryStatus, DeliveryStatusAtUtc FROM dbo.OutboundMessages WHERE Id = @id", conn);
        cmd.Parameters.AddWithValue("@id", messageId);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) throw new InvalidOperationException($"No queued email {messageId}.");
        return (Convert.ToInt32(r.GetValue(0)), r.IsDBNull(1) ? null : r.GetDateTime(1));
    }

    /// <summary>105 (VUL-20). A person's stored password hash and security stamp — what must never be shown or logged.</summary>
    public async Task<(string? PasswordHash, string? SecurityStamp)> UserSecretsAsync(string email)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand("SELECT PasswordHash, SecurityStamp FROM dbo.Users WHERE Email = @e AND IsDeleted = 0", conn);
        cmd.Parameters.AddWithValue("@e", email);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) throw new InvalidOperationException($"No person {email}.");
        return (r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1));
    }

    // =================================================================
    // 106 — VUL-21 … VUL-30
    // =================================================================

    private static readonly HashSet<(string Table, string Column)> TextColumns = new()
    {
        ("Contacts", "FirstName"), ("Contacts", "LastName"),
        ("Leads", "FullName"), ("Leads", "CompanyName"),
        ("Users", "FirstName"), ("Users", "LastName"),
        ("Tenants", "Name"),     // VUL-24 only: the test puts it back
        ("Invoices", "PublicLinkToken"),   // VUL-21: test servers can't email, and emailing is what makes an invoice's link
    };

    /// <summary>106 (VUL-24). A name written straight into the database, past every check. _E2E only.</summary>
    public async Task SetTextAsync(string table, string column, Guid id, string value)
    {
        if (!TextColumns.Contains((table, column))) throw new ArgumentOutOfRangeException(nameof(column), $"{table}.{column}");
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand($"UPDATE dbo.[{table}] SET [{column}] = @v WHERE Id = @id", conn);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@v", value);
        if (await cmd.ExecuteNonQueryAsync() != 1) throw new InvalidOperationException($"No {table} row {id} to change.");
    }

    public sealed record StoredOutbound(Guid Id, Guid TenantId, string ToAddress, string? ToName, string? ReplyToAddress,
        string? CcAddresses, string Subject, string BodyHtml);

    /// <summary>106 (VUL-24). An outgoing email exactly as it was queued.</summary>
    public async Task<StoredOutbound?> OutboundAsync(Guid messageId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT Id, TenantId, ToAddress, ToName, ReplyToAddress, CcAddresses, Subject, BodyHtml FROM dbo.OutboundMessages WHERE Id = @id", conn);
        cmd.Parameters.AddWithValue("@id", messageId);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;
        string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        return new StoredOutbound(r.GetGuid(0), r.GetGuid(1), S(2) ?? "", S(3), S(4), S(5), S(6) ?? "", S(7) ?? "");
    }

    /// <summary>106 (VUL-24). Every email queued for a workspace since a moment, newest first.</summary>
    public async Task<List<Guid>> OutboundSinceAsync(Guid tenantId, DateTime sinceUtc)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT Id FROM dbo.OutboundMessages WHERE TenantId = @t AND CreatedAtUtc >= @s ORDER BY CreatedAtUtc DESC", conn);
        cmd.Parameters.AddWithValue("@t", tenantId);
        cmd.Parameters.AddWithValue("@s", sinceUtc);
        var ids = new List<Guid>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) ids.Add(r.GetGuid(0));
        return ids;
    }

    public sealed record StoredInbound(Guid Id, Guid TenantId, int Status, int ProcessingState, Guid? LeadId, Guid? ContactId,
        bool? SenderVerified, string Subject, int AttachmentCount, string? MatchNote, string? ProcessingError);

    /// <summary>106 (VUL-25). A received email by the provider's id (whatever workspace it went to), or null.</summary>
    public async Task<StoredInbound?> InboundAsync(string providerEmailId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT Id, TenantId, Status, ProcessingState, LeadId, ContactId, SenderVerified, Subject, AttachmentCount, MatchNote, ProcessingError " +
            "FROM dbo.InboundEmails WHERE ProviderEmailId = @p", conn);
        cmd.Parameters.AddWithValue("@p", providerEmailId);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;
        return new StoredInbound(r.GetGuid(0), r.GetGuid(1), Convert.ToInt32(r.GetValue(2)), Convert.ToInt32(r.GetValue(3)),
            r.IsDBNull(4) ? null : r.GetGuid(4), r.IsDBNull(5) ? null : r.GetGuid(5),
            r.IsDBNull(6) ? null : r.GetBoolean(6), r.IsDBNull(7) ? "" : r.GetString(7),
            Convert.ToInt32(r.GetValue(8)), r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10));
    }

    public sealed record StoredInboundFile(string FileName, string FileUrl, string? MimeType);

    /// <summary>106 (VUL-25). The attachments kept from a received email.</summary>
    public async Task<List<StoredInboundFile>> InboundFilesAsync(Guid inboundEmailId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT FileName, FileUrl, MimeType FROM dbo.InboundEmailAttachments WHERE InboundEmailId = @id", conn);
        cmd.Parameters.AddWithValue("@id", inboundEmailId);
        var list = new List<StoredInboundFile>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new StoredInboundFile(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2)));
        return list;
    }

    public sealed record StoredAudit(Guid Id, Guid? TenantId, string EntityType, Guid? EntityId, string Action, string By,
        Guid? ActorUserId, string? Data, DateTime CreatedAtUtc);

    /// <summary>106 (VUL-27/28). Audit rows with this action since a moment, newest first.</summary>
    public async Task<List<StoredAudit>> AuditAsync(string action, DateTime sinceUtc, Guid? tenantId = null)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT Id, TenantId, EntityType, EntityId, Action, [By], ActorUserId, Data, CreatedAtUtc FROM dbo.AuditLogs " +
            "WHERE Action = @a AND CreatedAtUtc >= @s AND (@t IS NULL OR TenantId = @t) ORDER BY CreatedAtUtc DESC", conn);
        cmd.Parameters.AddWithValue("@a", action);
        cmd.Parameters.AddWithValue("@s", sinceUtc);
        cmd.Parameters.Add(new SqlParameter("@t", System.Data.SqlDbType.UniqueIdentifier) { Value = (object?)tenantId ?? DBNull.Value });
        var list = new List<StoredAudit>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
            list.Add(new StoredAudit(r.GetGuid(0), r.IsDBNull(1) ? null : r.GetGuid(1), r.GetString(2),
                r.IsDBNull(3) ? null : r.GetGuid(3), r.GetString(4), r.GetString(5),
                r.IsDBNull(6) ? null : r.GetGuid(6), r.IsDBNull(7) ? null : r.GetString(7), r.GetDateTime(8)));
        return list;
    }

    /// <summary>
    /// 106 (VUL-28). Tries to change, then to delete, one audit row — the
    /// way someone at the database would — inside a transaction that is
    /// ALWAYS rolled back. Returns what SQL Server said each time, or null
    /// when it let it happen. _E2E only.
    /// </summary>
    public async Task<(string? Update, string? Delete)> TryTamperWithAuditAsync(Guid auditId)
    {
        async Task<string?> Attempt(string sql)
        {
            await using var conn = await OpenAsync();
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();
            try
            {
                await using var cmd = new SqlCommand(sql, conn, tx);
                cmd.Parameters.AddWithValue("@id", auditId);
                await cmd.ExecuteNonQueryAsync();
                return null;
            }
            catch (SqlException ex)
            {
                return ex.Message;
            }
            finally
            {
                try { await tx.RollbackAsync(); } catch (InvalidOperationException) { /* already rolled back by the error */ }
            }
        }

        var update = await Attempt("UPDATE dbo.AuditLogs SET [By] = N'tampered' WHERE Id = @id");
        var delete = await Attempt("DELETE FROM dbo.AuditLogs WHERE Id = @id");
        return (update, delete);
    }

    // ── 109: markets (MarketTests) and other browsers (CrossBrowserTests) ──

    /// <summary>A workspace's money, tax and clock settings: its own, and its country's (Countries).</summary>
    public sealed record MarketSetup(
        string? Country, string? CurrencyCode, string? CurrencySymbol, int CurrencyDecimals,
        string? TaxLabel, decimal? DefaultTaxRate, string? NumberFormat, string? DateFormat, string? TimeFormat,
        string? CountryTimezone, string? WorkspaceCurrency, string? WorkspaceTimezone);

    public async Task<MarketSetup> MarketSetupAsync(Guid tenantId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            @"SELECT c.Name, c.CurrencyCode, c.CurrencySymbol, c.CurrencyDecimals, c.TaxLabel, c.DefaultTaxRate,
                     c.NumberFormat, c.DateFormat, c.TimeFormat, c.Timezone, t.DefaultCurrency, t.Timezone
              FROM Tenants t LEFT JOIN Countries c ON c.Id = t.CountryId
              WHERE t.Id = @t", conn);
        cmd.Parameters.AddWithValue("@t", tenantId);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) throw new InvalidOperationException($"No workspace {tenantId}.");

        string? S(int i) => r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i));
        return new MarketSetup(
            S(0), S(1), S(2), r.IsDBNull(3) ? 2 : Convert.ToInt32(r.GetValue(3)),
            S(4), r.IsDBNull(5) ? null : Convert.ToDecimal(r.GetValue(5)), S(6), S(7), S(8), S(9), S(10), S(11));
    }

    /// <summary>The deal's expected close date as stored (UTC), or null.</summary>
    public async Task<DateTime?> DealCloseDateAsync(Guid dealId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand("SELECT ExpectedCloseDateUtc FROM Deals WHERE Id = @id", conn);
        cmd.Parameters.AddWithValue("@id", dealId);
        var v = await cmd.ExecuteScalarAsync();
        return v is DateTime d ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null;
    }

    /// <summary>The keys of the workspace's OPEN stages (Category 0 = Open; Won and Lost are not).</summary>
    public async Task<HashSet<string>> OpenStageKeysAsync(Guid tenantId)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT [Key] FROM PipelineStages WHERE TenantId = @t AND Category = 0", conn);
        cmd.Parameters.AddWithValue("@t", tenantId);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) keys.Add(r.GetString(0));
        return keys;
    }
}
