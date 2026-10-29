// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Infrastructure/PerfData.cs
//
// NEW (107 — performance tests). The database side of the performance
// workspace. _E2E ONLY, like TestDatabase: every method checks the
// database's name first.
//
//   • Finding the workspace, giving its people the test password again,
//     and keeping its trial and limits usable (KeepUsableAsync).
//   • SeedAsync — fills it with a big, realistic set of records, ONCE.
//   • Sample record ids for the detail pages, row counts for the report,
//     and SQL Server's own "indexes I wished I had" list.
//
// HOW THE RECORDS ARE MADE (SeedAsync)
//   Going through the API 300,000 times would take hours. Instead the
//   seeder first makes ONE of each record through the API — a company, a
//   contact, a lead, a deal, a quote with its line, an invoice with its
//   line — so each is exactly what the app itself writes. Then SQL copies
//   each "template" row N times in one statement, changing only what must
//   differ: the id, the name, the email, the number, the dates, the owner,
//   the stage — and pointing each copy at copied parents (a contact at one
//   of the 10,000 companies, a deal at one of the 30,000 contacts, ...).
//   Every other column is copied as the app wrote it, so nothing the app
//   needs is missing, and columns added in later rounds are copied too
//   without changing this file.
//
//   Columns in a UNIQUE index that are not set below get a suffix (text)
//   or + n (numbers), so copies never collide.
//
//   All of it runs in ONE transaction: a seeding that fails part way
//   leaves nothing behind, and the next run simply tries again. Audit rows
//   can be ADDED (the 106 trigger only stops changes and deletes), and a
//   rolled-back insert is not a delete, so the trigger is never involved.
// =====================================================================

using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;

namespace MerkaiTrial.E2E.Infrastructure;

public sealed class PerfData
{
    private readonly string _connectionString;

    /// <summary>
    /// 107-update2: every command here may wait up to 5 minutes (not the
    /// default 30 s). These are the TEST's own checks on a big workspace —
    /// the first one after SQL Server starts reads from a cold disk, and a
    /// slow test check must not be mistaken for a slow app. The app's own
    /// queries keep their 30-second limit and are what the tests time.
    /// </summary>
    public PerfData(string connectionString) =>
        _connectionString = new SqlConnectionStringBuilder(connectionString) { CommandTimeout = 300 }.ConnectionString;

    // ── How big ─────────────────────────────────────────────────────────

    public sealed record Sizes(int Companies, int Contacts, int Leads, int Deals, int Quotes, int QuoteLinesPerQuote,
                               int Invoices, int AuditRows)
    {
        /// <summary>What the user asked for in 107: "assume the data is bigger".</summary>
        public static Sizes Big { get; } = new(
            Companies: 10_000, Contacts: 30_000, Leads: 50_000, Deals: 20_000,
            Quotes: 10_000, QuoteLinesPerQuote: 2, Invoices: 5_000, AuditRows: 200_000);
    }

    /// <summary>One of each record, made through the API, that SQL copies.</summary>
    public sealed record Templates(Guid Company, Guid Contact, Guid Lead, Guid Deal, Guid Quote, Guid Invoice);

    /// <summary>Records in the middle of the copies, for the detail pages.</summary>
    public sealed record Samples(Guid Lead, Guid Deal, Guid Contact, Guid Company, Guid Quote, Guid Invoice,
                                 string LeadName, string DealTitle, string ContactName, string CompanyName,
                                 string QuoteNumber, string InvoiceNumber);

    // ── The workspace and its people ──────────────────────────────────

    public async Task<Guid?> TenantIdByNameAsync(string name)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT TOP 1 Id FROM Tenants WHERE Name = @n AND IsDeleted = 0 ORDER BY CreatedAtUtc", conn);
        cmd.Parameters.AddWithValue("@n", name);
        return await cmd.ExecuteScalarAsync() is Guid id ? id : null;
    }

    /// <summary>The test password again, unlocked, active, confirmed — like PrepareSuperAdminAsync, for one person.</summary>
    public async Task ResetPersonAsync(string email, string password)
    {
        var hash = new PasswordHasher<object>().HashPassword(new object(), password);
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            @"UPDATE Users SET
                  PasswordHash        = @hash,
                  SecurityStamp       = @stamp,
                  MustChangePassword  = 0,
                  FailedLoginCount    = 0,
                  LockoutEndUtc       = NULL,
                  IsActive            = 1,
                  EmailConfirmedAtUtc = COALESCE(EmailConfirmedAtUtc, SYSUTCDATETIME()),
                  UpdatedAtUtc        = SYSUTCDATETIME(),
                  UpdatedBy           = 'e2e-perf'
              WHERE Email = @e AND IsDeleted = 0", conn);
        cmd.Parameters.AddWithValue("@hash", hash);
        cmd.Parameters.AddWithValue("@stamp", Guid.NewGuid().ToString("N"));
        cmd.Parameters.AddWithValue("@e", email);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// The workspace stays usable run after run: active, not suspended, a
    /// trial (if it has one) with at least a month left, and limits far
    /// above what the tests add — so a limit is never what a timing measures.
    /// </summary>
    public async Task KeepUsableAsync(Guid tenantId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            @"UPDATE Tenants SET
                  IsActive = 1,
                  IsSuspended = 0,
                  TrialExpiresAt = CASE WHEN TrialExpiresAt IS NOT NULL AND TrialExpiresAt < DATEADD(DAY, 30, SYSUTCDATETIME())
                                        THEN DATEADD(DAY, 180, SYSUTCDATETIME()) ELSE TrialExpiresAt END
              WHERE Id = @t;
              UPDATE TenantSettings SET
                  MaxUsers = CASE WHEN MaxUsers < 25 THEN 25 ELSE MaxUsers END,
                  MaxLeads = 10000000,
                  MaxDeals = 10000000
              WHERE TenantId = @t;", conn);
        cmd.Parameters.AddWithValue("@t", tenantId);
        await cmd.ExecuteNonQueryAsync();
    }

    // ── Seeding ─────────────────────────────────────────────────────────

    /// <summary>How many of the copied leads are there? 0 = not seeded yet.</summary>
    public async Task<int> SeededLeadsAsync(Guid tenantId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT COUNT(*) FROM Leads WHERE TenantId = @t AND FullName LIKE 'Perf Lead %'", conn);
        cmd.Parameters.AddWithValue("@t", tenantId);
        return (int)(await cmd.ExecuteScalarAsync() ?? 0);
    }

    /// <summary>
    /// Copies the templates into a big workspace — see the file header.
    /// One transaction; <paramref name="log"/> hears each table as it is done.
    /// </summary>
    public async Task SeedAsync(Guid tenantId, Templates tpl, Sizes size, Action<string> log)
    {
        await using var conn = await OpenAsync();
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync();

        async Task RunAsync(string sql, params (string Name, object Value)[] args)
        {
            await using var cmd = new SqlCommand(sql, conn, tx) { CommandTimeout = 1800 };
            cmd.Parameters.AddWithValue("@t", tenantId);
            foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
            await cmd.ExecuteNonQueryAsync();
        }

        // A command WITH parameters runs inside sp_executesql, and a #temp
        // table made in there disappears when it ends. So every #table is
        // CREATED by a command without parameters (it then lives as long as
        // the connection) and FILLED by the parameterised one.
        async Task CreateAsync(string sql)
        {
            await using var cmd = new SqlCommand(sql, conn, tx) { CommandTimeout = 300 };
            await cmd.ExecuteNonQueryAsync();
        }

        async Task<int> CountAsync(string sql)
        {
            await using var cmd = new SqlCommand(sql, conn, tx) { CommandTimeout = 300 };
            cmd.Parameters.AddWithValue("@t", tenantId);
            return (int)(await cmd.ExecuteScalarAsync() ?? 0);
        }

        try
        {
            // ── Look-up lists the copies are spread over ──────────────
            await CreateAsync(@"
                CREATE TABLE #owners (rn int NOT NULL PRIMARY KEY, Id nvarchar(64) NOT NULL);
                CREATE TABLE #stages (rn int NOT NULL PRIMARY KEY, [Key] nvarchar(100) NOT NULL, Probability int NOT NULL, Category int NOT NULL);
                CREATE TABLE #leadStatuses (rn int NOT NULL PRIMARY KEY, [Key] nvarchar(100) NOT NULL);");

            await RunAsync(@"
                INSERT INTO #owners (rn, Id)
                SELECT ROW_NUMBER() OVER (ORDER BY Email), LOWER(CONVERT(nvarchar(36), Id))
                FROM Users WHERE TenantId = @t AND IsDeleted = 0 AND IsActive = 1;

                INSERT INTO #stages (rn, [Key], Probability, Category)
                SELECT ROW_NUMBER() OVER (ORDER BY SortOrder, [Key]), [Key], Probability, CAST(Category AS int)
                FROM PipelineStages WHERE TenantId = @t AND IsActive = 1;

                INSERT INTO #leadStatuses (rn, [Key])
                SELECT ROW_NUMBER() OVER (ORDER BY SortOrder, [Key]), [Key]
                FROM LeadStatusDefinitions
                WHERE TenantId = @t AND IsActive = 1 AND [Key] NOT LIKE '%convert%';");

            var owners = await CountAsync("SELECT COUNT(*) FROM #owners");
            var stages = await CountAsync("SELECT COUNT(*) FROM #stages");
            var statuses = await CountAsync("SELECT COUNT(*) FROM #leadStatuses");
            if (owners == 0) throw new InvalidOperationException("The performance workspace has no active people to own records.");
            if (stages == 0) throw new InvalidOperationException("The performance workspace has no active pipeline stages.");
            log($"spread over {owners} people, {stages} stages, {statuses} lead statuses");

            // A spread of dates over the last two years, different for every copy.
            const string Ago = "DATEADD(MINUTE, -((m.rn * 7919) % 1051200), SYSUTCDATETIME())";
            const string Num = "CAST(m.rn AS varchar(10))";
            const string Pad7 = "RIGHT('0000000' + CAST(m.rn AS varchar(10)), 7)";
            const string Pad8 = "RIGHT('00000000' + CAST(m.rn AS varchar(10)), 8)";

            // ── Companies ─────────────────────────────────────────────
            await CloneAsync(conn, tx, tenantId, "Companies", "Companies", size.Companies, tpl.Company, log, new()
            {
                ["Name"] = $"'Perf Company ' + {Num}",
                ["CreatedAtUtc"] = Ago, ["UpdatedAtUtc"] = Ago, ["IsDeleted"] = "0",
            });

            // ── Contacts — each at one of the companies ─────────────────
            await CloneAsync(conn, tx, tenantId, "Contacts", "Contacts", size.Contacts, tpl.Contact, log, new()
            {
                ["CompanyId"] = "co.Id",
                ["FirstName"] = "'Perf'",
                ["LastName"] = $"'Contact ' + {Num}",
                ["Email"] = $"'perf.c' + {Num} + '@perf-e2e.example.com'",
                ["Phone"] = $"'+9191' + {Pad8}",
                ["IsPrimary"] = $"CASE WHEN m.rn <= {size.Companies} THEN 1 ELSE 0 END",
                ["CreatedAtUtc"] = Ago, ["UpdatedAtUtc"] = Ago, ["IsDeleted"] = "0",
            }, $"JOIN #map_Companies co ON co.rn = ((m.rn - 1) % {size.Companies}) + 1");

            // ── Leads — owners and statuses spread evenly ───────────────
            var leadSet = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["FullName"] = $"'Perf Lead ' + {Num}",
                ["Email"] = $"'perf.l' + {Num} + '@perf-e2e.example.com'",
                ["Phone"] = $"'+9190' + {Pad8}",
                ["CompanyName"] = $"'Perf Lead Co ' + CAST(m.rn % 5000 AS varchar(10))",
                ["EstimatedValue"] = "1000 + ((m.rn * 37) % 500000)",
                ["OwnerUserId"] = "o.Id",
                ["Score"] = "(m.rn * 13) % 100",
                ["IsConverted"] = "0", ["ContactId"] = "NULL", ["CompanyId"] = "NULL", ["DealId"] = "NULL",
                ["ConvertedToContactId"] = "NULL", ["ConvertedToCompanyId"] = "NULL", ["ConvertedAtUtc"] = "NULL",
                ["CreatedAtUtc"] = Ago, ["UpdatedAtUtc"] = Ago, ["IsDeleted"] = "0",
            };
            var leadJoins = $"JOIN #owners o ON o.rn = ((m.rn - 1) % {owners}) + 1";
            if (statuses > 0)
            {
                leadSet["Status"] = "ls.[Key]";
                leadJoins += $" JOIN #leadStatuses ls ON ls.rn = ((m.rn - 1) % {statuses}) + 1";
            }
            await CloneAsync(conn, tx, tenantId, "Leads", "Leads", size.Leads, tpl.Lead, log, leadSet, leadJoins);

            // ── Deals — on a contact (and that contact's company), every stage ──
            await CloneAsync(conn, tx, tenantId, "Deals", "Deals", size.Deals, tpl.Deal, log, new()
            {
                ["Title"] = $"'Perf Deal ' + {Num}",
                ["ContactId"] = "ct.Id",
                ["CompanyId"] = "co.Id",
                ["LeadId"] = "NULL",
                ["Stage"] = "s.[Key]",
                ["Probability"] = "s.Probability",
                ["ExpectedValue"] = "5000 + ((m.rn * 53) % 900000)",
                ["ActualValue"] = "CASE WHEN s.Category = 1 THEN 5000 + ((m.rn * 53) % 900000) ELSE NULL END",
                ["ExpectedCloseDateUtc"] = "CAST(DATEADD(DAY, (m.rn % 240) - 120, SYSUTCDATETIME()) AS date)",
                ["ActualCloseDateUtc"] = "CASE WHEN s.Category <> 0 THEN DATEADD(DAY, -(m.rn % 300), SYSUTCDATETIME()) ELSE NULL END",
                ["LostReason"] = "CASE WHEN s.Category = 2 THEN 'Perf: price' ELSE NULL END",
                ["OwnerUserId"] = "o.Id",
                ["CreatedAtUtc"] = Ago, ["UpdatedAtUtc"] = Ago, ["IsDeleted"] = "0",
                ["DeletedAtUtc"] = "NULL", ["DeletedBy"] = "NULL",
            }, $@"JOIN #map_Contacts ct ON ct.rn = ((m.rn - 1) % {size.Contacts}) + 1
                  JOIN #map_Companies co ON co.rn = ((ct.rn - 1) % {size.Companies}) + 1
                  JOIN #stages s ON s.rn = ((m.rn - 1) % {stages}) + 1
                  JOIN #owners o ON o.rn = ((m.rn - 1) % {owners}) + 1");

            // ── Each deal's first stage-history row ─────────────────────
            var history = await FirstChildAsync(conn, tx, "DealStageHistory", "DealId", tpl.Deal, "ChangedAtUtc");
            if (history is { } h)
            {
                await CloneAsync(conn, tx, tenantId, "DealStageHistory", "History", size.Deals, h, log, new()
                {
                    ["DealId"] = "d.Id",
                    ["ToStage"] = "dd.Stage",
                    ["ChangedAtUtc"] = "dd.CreatedAtUtc",
                }, "JOIN #map_Deals d ON d.rn = m.rn JOIN dbo.Deals dd ON dd.Id = d.Id");
            }
            else log("DealStageHistory: the template deal has no history row — skipped");

            // ── Quotes — on different deals, in four statuses ──────────
            await CloneAsync(conn, tx, tenantId, "Quotes", "Quotes", size.Quotes, tpl.Quote, log, new()
            {
                ["DealId"] = "d.Id",
                ["Number"] = $"'PQ-' + {Pad7}",
                ["Status"] = "CHOOSE((m.rn % 4) + 1, 0, 1, 3, 4)",
                ["IssueDateUtc"] = "DATEADD(DAY, -(m.rn % 700), SYSUTCDATETIME())",
                ["ExpiresAtUtc"] = "DATEADD(DAY, 30 - (m.rn % 700), SYSUTCDATETIME())",
                ["PublicLinkToken"] = "NULL", ["PaymentLinkUrl"] = "NULL", ["PdfUrl"] = "NULL",
                ["CreatedAtUtc"] = "DATEADD(DAY, -(m.rn % 700), SYSUTCDATETIME())", ["UpdatedAtUtc"] = Ago, ["IsDeleted"] = "0",
            }, $"JOIN #map_Deals d ON d.rn = ((m.rn - 1) % {size.Deals}) + 1");

            var quoteLine = await FirstChildAsync(conn, tx, "QuoteItems", "QuoteId", tpl.Quote);
            if (quoteLine is { } ql)
            {
                await CloneAsync(conn, tx, tenantId, "QuoteItems", "QuoteItems", size.Quotes * size.QuoteLinesPerQuote, ql, log, new()
                {
                    ["QuoteId"] = "q.Id",
                    ["Name"] = $"'Perf item ' + {Num}",
                    ["CreatedAtUtc"] = Ago, ["IsDeleted"] = "0",
                }, $"JOIN #map_Quotes q ON q.rn = ((m.rn - 1) % {size.Quotes}) + 1");
            }

            // ── Invoices — issued, paid and overdue ────────────────────
            await CloneAsync(conn, tx, tenantId, "Invoices", "Invoices", size.Invoices, tpl.Invoice, log, new()
            {
                ["DealId"] = "d.Id",
                ["QuoteId"] = "NULL",
                ["Number"] = $"'PI-' + {Pad7}",
                ["Status"] = "CHOOSE((m.rn % 3) + 1, 1, 4, 5)",
                ["Balance"] = "CASE WHEN m.rn % 3 = 1 THEN 0 ELSE t.Balance END",
                ["IssueDateUtc"] = "DATEADD(DAY, -(m.rn % 700), SYSUTCDATETIME())",
                ["IssuedAtUtc"] = "DATEADD(DAY, -(m.rn % 700), SYSUTCDATETIME())",
                ["DueDateUtc"] = "DATEADD(DAY, 14 - (m.rn % 700), SYSUTCDATETIME())",
                ["PublicLinkToken"] = "NULL", ["PdfUrl"] = "NULL", ["CheckoutUrl"] = "NULL",
                ["MilestoneId"] = "NULL", ["MilestoneName"] = "NULL", ["MilestoneSequence"] = "NULL",
                ["MilestoneCount"] = "NULL", ["MilestonePercent"] = "NULL",
                ["CreatedAtUtc"] = "DATEADD(DAY, -(m.rn % 700), SYSUTCDATETIME())", ["UpdatedAtUtc"] = Ago, ["IsDeleted"] = "0",
            }, $"JOIN #map_Deals d ON d.rn = ((m.rn * 3 - 1) % {size.Deals}) + 1");

            var invoiceLine = await FirstChildAsync(conn, tx, "InvoiceLines", "InvoiceId", tpl.Invoice);
            if (invoiceLine is { } il)
            {
                await CloneAsync(conn, tx, tenantId, "InvoiceLines", "InvoiceLines", size.Invoices, il, log, new()
                {
                    ["InvoiceId"] = "i.Id",
                    ["Name"] = $"'Perf invoice item ' + {Num}",
                    ["CreatedAtUtc"] = Ago, ["IsDeleted"] = "0",
                }, "JOIN #map_Invoices i ON i.rn = m.rn");
            }

            // ── Audit log — about the copied leads, over two years ─────
            var audit = await FirstChildAsync(conn, tx, "AuditLogs", "EntityId", tpl.Lead);
            if (audit is { } a)
            {
                await CloneAsync(conn, tx, tenantId, "AuditLogs", "Audit", size.AuditRows, a, log, new()
                {
                    ["EntityId"] = "l.Id",
                    ["CreatedAtUtc"] = Ago,
                }, $"JOIN #map_Leads l ON l.rn = ((m.rn - 1) % {size.Leads}) + 1");
            }
            else log("AuditLogs: the template lead has no audit row — skipped");

            await tx.CommitAsync();
        }
        catch
        {
            try { await tx.RollbackAsync(); } catch { /* the original error matters */ }
            throw;
        }

        // Fresh statistics, so the first page of the run is not the one
        // that pays for SQL Server noticing the table grew ten times.
        foreach (var table in new[] { "Companies", "Contacts", "Leads", "Deals", "DealStageHistory", "Quotes",
                                      "QuoteItems", "Invoices", "InvoiceLines", "AuditLogs" })
        {
            await using var stats = new SqlCommand($"UPDATE STATISTICS dbo.[{table}]", conn) { CommandTimeout = 600 };
            await stats.ExecuteNonQueryAsync();
        }
        log("statistics updated");
    }

    /// <summary>The oldest row in <paramref name="table"/> whose <paramref name="parentColumn"/> is the template, or null.</summary>
    private static async Task<Guid?> FirstChildAsync(SqlConnection conn, SqlTransaction tx, string table, string parentColumn, Guid parent,
        string orderBy = "CreatedAtUtc")
    {
        await using var cmd = new SqlCommand(
            $"SELECT TOP 1 Id FROM dbo.[{table}] WHERE [{parentColumn}] = @p ORDER BY [{orderBy}]", conn, tx);
        cmd.Parameters.AddWithValue("@p", parent);
        return await cmd.ExecuteScalarAsync() is Guid id ? id : null;
    }

    private sealed record Column(string Name, string Type, int MaxLength, bool Skip);

    /// <summary>
    /// Copies one template row <paramref name="count"/> times. The new ids
    /// go in #map_{mapName} (rn → Id), so later tables can point at them.
    /// <paramref name="set"/> names a SQL expression per column; t is the
    /// template row, m.rn the copy's number (1…count), m.Id its new id.
    /// </summary>
    private static async Task CloneAsync(SqlConnection conn, SqlTransaction tx, Guid tenantId, string table, string mapName,
        int count, Guid templateId, Action<string> log, Dictionary<string, string> set, string joins = "")
    {
        var started = DateTime.UtcNow;
        var columns = new List<Column>();
        await using (var cmd = new SqlCommand(
            @"SELECT c.name, ty.name, c.max_length,
                     CAST(CASE WHEN c.is_computed = 1 OR c.is_identity = 1 OR ty.name IN ('timestamp', 'rowversion') THEN 1 ELSE 0 END AS bit)
              FROM sys.columns c JOIN sys.types ty ON ty.user_type_id = c.user_type_id
              WHERE c.object_id = OBJECT_ID(@tbl) ORDER BY c.column_id", conn, tx))
        {
            cmd.Parameters.AddWithValue("@tbl", "dbo." + table);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                columns.Add(new Column(r.GetString(0), r.GetString(1), r.GetInt16(2), r.GetBoolean(3)));
        }
        if (columns.Count == 0) throw new InvalidOperationException($"No table dbo.{table} in the test database.");

        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = new SqlCommand(
            @"SELECT DISTINCT c.name
              FROM sys.indexes i
              JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
              JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
              WHERE i.object_id = OBJECT_ID(@tbl) AND i.is_unique = 1 AND i.is_primary_key = 0", conn, tx))
        {
            cmd.Parameters.AddWithValue("@tbl", "dbo." + table);
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) unique.Add(r.GetString(0));
        }

        var names = new List<string>();
        var values = new List<string>();
        foreach (var c in columns.Where(c => !c.Skip))
        {
            names.Add($"[{c.Name}]");
            values.Add(ExpressionFor(c, set, unique));
        }

        // Created without parameters so it outlives this command (see SeedAsync).
        await using (var create = new SqlCommand(
            $"IF OBJECT_ID('tempdb..#map_{mapName}') IS NOT NULL DROP TABLE #map_{mapName};\n" +
            $"CREATE TABLE #map_{mapName} (rn int NOT NULL PRIMARY KEY, Id uniqueidentifier NOT NULL);", conn, tx))
        {
            await create.ExecuteNonQueryAsync();
        }

        var sql = new StringBuilder()
            .Append($"INSERT INTO #map_{mapName} (rn, Id)\n")
            .Append("SELECT n, NEWID() FROM (SELECT TOP (@count) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n\n")
            .Append("       FROM sys.all_objects a CROSS JOIN sys.all_objects b) x;\n")
            .Append($"INSERT INTO dbo.[{table}] ({string.Join(", ", names)})\n")
            .Append($"SELECT {string.Join(",\n       ", values)}\n")
            .Append($"FROM dbo.[{table}] t\nJOIN #map_{mapName} m ON 1 = 1\n{joins}\n")
            .Append("WHERE t.Id = @tpl;");

        await using (var cmd = new SqlCommand(sql.ToString(), conn, tx) { CommandTimeout = 1800 })
        {
            cmd.Parameters.AddWithValue("@count", count);
            cmd.Parameters.AddWithValue("@tpl", templateId);
            cmd.Parameters.AddWithValue("@t", tenantId);
            // Both inserts count: the map's rows, then the copies. Fewer than
            // twice the number means the template row was not found.
            var added = await cmd.ExecuteNonQueryAsync();
            if (added < 2 * count)
                throw new InvalidOperationException(
                    $"Copying {table}: {added - count} copies written, expected {count}. Is the template row {templateId} there?");
        }

        log($"{table}: {count:N0} added in {(DateTime.UtcNow - started).TotalSeconds:0.0}s" +
            (unique.Count > 0 ? $" (unique: {string.Join(", ", unique)})" : ""));
    }

    private static string ExpressionFor(Column c, Dictionary<string, string> set, HashSet<string> unique)
    {
        var isText = c.Type is "nvarchar" or "varchar" or "nchar" or "char";
        var chars = c.MaxLength < 0 ? -1 : c.Type.StartsWith('n') ? c.MaxLength / 2 : c.MaxLength;

        if (string.Equals(c.Name, "Id", StringComparison.OrdinalIgnoreCase) && c.Type == "uniqueidentifier")
            return "m.Id";

        if (set.TryGetValue(c.Name, out var expression))
            return isText && chars > 0 && expression != "NULL" ? $"LEFT({expression}, {chars})" : expression;

        if (unique.Contains(c.Name) && !string.Equals(c.Name, "TenantId", StringComparison.OrdinalIgnoreCase))
        {
            if (isText)
            {
                var keep = chars < 0 ? 400 : Math.Max(0, chars - 9);
                return $"CASE WHEN t.[{c.Name}] IS NULL THEN NULL ELSE LEFT(t.[{c.Name}], {keep}) + '-' + RIGHT('00000000' + CAST(m.rn AS varchar(10)), 8) END";
            }
            if (c.Type is "int" or "bigint") return $"t.[{c.Name}] + m.rn";
        }

        return $"t.[{c.Name}]";
    }

    // ── Read-backs for the tests and the report ─────────────────────────

    public async Task<Samples> SamplesAsync(Guid tenantId, Sizes size)
    {
        await using var conn = await OpenAsync();

        async Task<(Guid Id, string Text)> OneAsync(string sql, string text)
        {
            await using var cmd = new SqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@t", tenantId);
            cmd.Parameters.AddWithValue("@x", text);
            return await cmd.ExecuteScalarAsync() is Guid id
                ? (id, text)
                : throw new InvalidOperationException($"The performance workspace has no record '{text}' — was it seeded?");
        }

        var lead = await OneAsync("SELECT TOP 1 Id FROM Leads WHERE TenantId = @t AND FullName = @x", $"Perf Lead {size.Leads / 2}");
        var deal = await OneAsync("SELECT TOP 1 Id FROM Deals WHERE TenantId = @t AND Title = @x", $"Perf Deal {size.Deals / 2}");
        var contact = await OneAsync("SELECT TOP 1 Id FROM Contacts WHERE TenantId = @t AND LastName = @x", $"Contact {size.Contacts / 2}");
        var company = await OneAsync("SELECT TOP 1 Id FROM Companies WHERE TenantId = @t AND Name = @x", $"Perf Company {size.Companies / 2}");
        var quote = await OneAsync("SELECT TOP 1 Id FROM Quotes WHERE TenantId = @t AND Number = @x", $"PQ-{size.Quotes / 2:0000000}");
        var invoice = await OneAsync("SELECT TOP 1 Id FROM Invoices WHERE TenantId = @t AND Number = @x", $"PI-{size.Invoices / 2:0000000}");

        return new Samples(lead.Id, deal.Id, contact.Id, company.Id, quote.Id, invoice.Id,
            lead.Text, deal.Text, "Perf " + contact.Text, company.Text, quote.Text, invoice.Text);
    }

    /// <summary>Live rows of the workspace in the tables the tests read, for the report.</summary>
    public async Task<List<(string Table, int Rows)>> RowCountsAsync(Guid tenantId)
    {
        var list = new List<(string, int)>();
        await using var conn = await OpenAsync();
        foreach (var table in new[] { "Companies", "Contacts", "Leads", "Deals", "Quotes", "QuoteItems", "Invoices", "InvoiceLines", "AuditLogs" })
        {
            var deleted = table is "AuditLogs" or "QuoteItems" or "InvoiceLines" ? "" : " AND IsDeleted = 0";
            await using var cmd = new SqlCommand($"SELECT COUNT(*) FROM dbo.[{table}] WHERE TenantId = @t{deleted}", conn) { CommandTimeout = 120 };
            cmd.Parameters.AddWithValue("@t", tenantId);
            list.Add((table, (int)(await cmd.ExecuteScalarAsync() ?? 0)));
        }
        return list;
    }

    /// <summary>The test database's size on disk, in MB (data + log).</summary>
    public async Task<int> DatabaseSizeMbAsync()
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand("SELECT CAST(SUM(CAST(size AS bigint)) * 8 / 1024 AS int) FROM sys.database_files", conn);
        return (int)(await cmd.ExecuteScalarAsync() ?? 0);
    }

    /// <summary>
    /// SQL Server's own list of indexes it would have used, for THIS
    /// database, biggest expected gain first. Collected by SQL Server since
    /// it last started; it never changes anything. Needs VIEW SERVER STATE
    /// (a Windows admin on their own SQL Server has it).
    /// </summary>
    public async Task<List<string>> MissingIndexesAsync(int top = 15)
    {
        var list = new List<string>();
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            @"SELECT TOP (@top)
                     OBJECT_NAME(d.object_id, d.database_id) AS TableName,
                     ISNULL(d.equality_columns, '') AS Eq,
                     ISNULL(d.inequality_columns, '') AS Ineq,
                     ISNULL(d.included_columns, '') AS Incl,
                     s.user_seeks + s.user_scans AS Uses,
                     CAST(s.avg_user_impact AS decimal(5,1)) AS Impact,
                     CAST(s.avg_total_user_cost * s.avg_user_impact * (s.user_seeks + s.user_scans) AS decimal(18,0)) AS Score
              FROM sys.dm_db_missing_index_details d
              JOIN sys.dm_db_missing_index_groups g ON g.index_handle = d.index_handle
              JOIN sys.dm_db_missing_index_group_stats s ON s.group_handle = g.index_group_handle
              WHERE d.database_id = DB_ID()
              ORDER BY Score DESC", conn);
        cmd.Parameters.AddWithValue("@top", top);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var on = string.Join(", ", new[] { r.GetString(1), r.GetString(2) }.Where(s => s.Length > 0));
            var incl = r.GetString(3);
            list.Add($"{r.GetString(0)} ON ({on})" + (incl.Length > 0 ? $" INCLUDE ({incl})" : "") +
                     $" — used {r.GetInt64(4):N0} times, would save about {r.GetDecimal(5)}% of those queries' cost");
        }
        return list;
    }

    private async Task<SqlConnection> OpenAsync()
    {
        var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync();
        await using var check = new SqlCommand("SELECT DB_NAME()", conn);
        var name = (string?)await check.ExecuteScalarAsync() ?? "";
        if (!name.EndsWith("_E2E", StringComparison.OrdinalIgnoreCase))
        {
            await conn.DisposeAsync();
            throw new InvalidOperationException($"Refusing to touch database '{name}': the performance data goes only into a database whose name ends in _E2E.");
        }
        return conn;
    }
}
