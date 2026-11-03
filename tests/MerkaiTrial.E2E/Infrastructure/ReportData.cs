// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Infrastructure/ReportData.cs
//
// NEW (111). Puts records made through the API into the exact state a
// report test needs: a deal that was won on a given day and edited later,
// a deal opened 14 months ago, an invoice issued last month and paid this
// month, a record in another currency.
//
// Why straight to the database: these are DATES IN THE PAST and states the
// screens get to only over weeks (a payment that arrives a month after the
// invoice). The records themselves are made through the API first; only
// their dates, stage and currency are set here. Workspace R only — nothing
// else uses it — and only ever the test database (_E2E).
// =====================================================================

using Microsoft.Data.SqlClient;

namespace MerkaiTrial.E2E.Infrastructure;

public static class ReportData
{
    private static async Task<SqlConnection> OpenAsync()
    {
        var conn = new SqlConnection(E2ESettings.ConnectionString);
        await conn.OpenAsync();
        await using var check = new SqlCommand("SELECT DB_NAME()", conn);
        var name = (string?)await check.ExecuteScalarAsync() ?? "";
        if (!name.EndsWith("_E2E", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"ReportData only writes to an _E2E database, not '{name}'.");
        return conn;
    }

    private static async Task ExecAsync(string sql, params (string Name, object? Value)[] args)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        var rows = await cmd.ExecuteNonQueryAsync();
        if (rows == 0) throw new InvalidOperationException($"ReportData changed no rows: {sql}");
    }

    /// <summary>A deal's stage, probability, dates and (optionally) currency and vertical.</summary>
    public static Task SetDealAsync(Guid dealId, string stage, int probability, decimal value, DateTime createdUtc,
        DateTime? closedUtc, DateTime updatedUtc, string? currency = null, Guid? verticalId = null, Guid? leadId = null,
        DateTime? expectedCloseUtc = null) =>
        ExecAsync(@"UPDATE dbo.Deals
                       SET Stage = @stage, Probability = @p, ExpectedValue = @value, CreatedAtUtc = @created,
                           ActualCloseDateUtc = @closed, UpdatedAtUtc = @updated,
                           Currency = COALESCE(@currency, Currency),
                           VerticalId = COALESCE(@vertical, VerticalId),
                           LeadId = COALESCE(@lead, LeadId),
                           ExpectedCloseDateUtc = COALESCE(@expclose, ExpectedCloseDateUtc)
                     WHERE Id = @id",
            ("@stage", stage), ("@p", probability), ("@value", value), ("@created", createdUtc), ("@closed", closedUtc),
            ("@updated", updatedUtc), ("@currency", currency), ("@vertical", verticalId), ("@lead", leadId), ("@expclose", expectedCloseUtc), ("@id", dealId));

    /// <summary>A lead's creation date, converted flag and (optionally) currency.</summary>
    public static Task SetLeadAsync(Guid leadId, DateTime createdUtc, bool converted = false, string? currency = null) =>
        ExecAsync(@"UPDATE dbo.Leads
                       SET CreatedAtUtc = @created, IsConverted = @converted,
                           Currency = COALESCE(@currency, Currency)
                     WHERE Id = @id",
            ("@created", createdUtc), ("@converted", converted), ("@currency", currency), ("@id", leadId));

    /// <summary>An invoice's issue and due dates.</summary>
    public static Task SetInvoiceDatesAsync(Guid invoiceId, DateTime issuedUtc, DateTime? dueUtc) =>
        ExecAsync("UPDATE dbo.Invoices SET IssueDateUtc = @issued, DueDateUtc = @due WHERE Id = @id",
            ("@issued", issuedUtc), ("@due", dueUtc), ("@id", invoiceId));

    /// <summary>The date the (only) payment on an invoice arrived.</summary>
    public static Task SetPaymentDateAsync(Guid invoiceId, DateTime paidUtc) =>
        ExecAsync("UPDATE dbo.Payments SET PaidAtUtc = @paid WHERE InvoiceId = @id AND IsDeleted = 0",
            ("@paid", paidUtc), ("@id", invoiceId));

    /// <summary>A global, active vertical's id and name — for the Vertical filter.</summary>
    public static async Task<(Guid Id, string Name)> AnyVerticalAsync()
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT TOP 1 Id, Name FROM dbo.CompanyVerticals WHERE TenantId IS NULL AND IsActive = 1 AND IsDeleted = 0 ORDER BY DisplayOrder, Name",
            conn);
        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) throw new InvalidOperationException("There is no active global vertical to filter by.");
        return (r.GetGuid(0), r.GetString(1));
    }

    /// <summary>The key of a workspace's stage with this name.</summary>
    public static async Task<string> StageKeyAsync(Guid tenantId, string name)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand("SELECT TOP 1 [Key] FROM dbo.PipelineStages WHERE TenantId = @t AND Name = @n", conn);
        cmd.Parameters.AddWithValue("@t", tenantId);
        cmd.Parameters.AddWithValue("@n", name);
        return (string?)await cmd.ExecuteScalarAsync()
               ?? throw new InvalidOperationException($"Workspace has no stage called '{name}'.");
    }

    // ── 112 ───────────────────────────────────────────────────────────

    private static async Task<int> RunAsync(string sql, params (string Name, object? Value)[] args)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Forgets everything that "touched" a deal — its stage history, its
    /// logged activities and its notes — so a test can say exactly when
    /// it last moved and when something was last logged.
    /// </summary>
    public static async Task ClearDealTouchesAsync(Guid dealId)
    {
        await RunAsync("DELETE FROM dbo.DealStageHistory WHERE DealId = @id", ("@id", dealId));
        await RunAsync("DELETE FROM dbo.Activities WHERE EntityType = 'Deal' AND EntityId = @id", ("@id", dealId));
        await RunAsync("DELETE FROM dbo.DealNotes WHERE DealId = @id", ("@id", dealId));
    }

    /// <summary>A stage change on a given day.</summary>
    public static Task AddStageChangeAsync(Guid tenantId, Guid dealId, string from, string to, DateTime whenUtc) =>
        ExecAsync(@"INSERT INTO dbo.DealStageHistory (Id, TenantId, DealId, FromStage, ToStage, ChangedAtUtc, ChangedBy, Note)
                    VALUES (NEWID(), @t, @d, @from, @to, @when, 'e2e', NULL)",
            ("@t", tenantId), ("@d", dealId), ("@from", from), ("@to", to), ("@when", whenUtc));

    /// <summary>A note on a deal, written on a given day.</summary>
    public static Task AddDealNoteAsync(Guid tenantId, Guid dealId, DateTime whenUtc) =>
        ExecAsync(@"INSERT INTO dbo.DealNotes (Id, TenantId, DealId, Note, CreatedAtUtc, CreatedBy, IsDeleted)
                    VALUES (NEWID(), @t, @d, N'E2E note', @when, 'e2e', 0)",
            ("@t", tenantId), ("@d", dealId), ("@when", whenUtc));

    /// <summary>A quote's status (the QuoteStatus number), money and dates.</summary>
    public static Task SetQuoteAsync(Guid quoteId, int status, decimal subtotal, decimal discount, decimal grandTotal,
        DateTime issuedUtc, DateTime expiresUtc, string? currency = null) =>
        ExecAsync(@"UPDATE dbo.Quotes
                       SET Status = @s, Subtotal = @sub, DiscountTotal = @disc, TaxTotal = 0, GrandTotal = @grand,
                           IssueDateUtc = @issued, ExpiresAtUtc = @expires, Currency = COALESCE(@currency, Currency)
                     WHERE Id = @id",
            ("@s", status), ("@sub", subtotal), ("@disc", discount), ("@grand", grandTotal),
            ("@issued", issuedUtc), ("@expires", expiresUtc), ("@currency", currency), ("@id", quoteId));

    /// <summary>A lead's status key, creation date and conversion.</summary>
    public static Task SetLeadStateAsync(Guid leadId, string statusKey, DateTime createdUtc, DateTime? convertedUtc) =>
        ExecAsync(@"UPDATE dbo.Leads
                       SET Status = @status, CreatedAtUtc = @created,
                           IsConverted = CASE WHEN @converted IS NULL THEN 0 ELSE 1 END, ConvertedAtUtc = @converted
                     WHERE Id = @id",
            ("@status", statusKey), ("@created", createdUtc), ("@converted", convertedUtc), ("@id", leadId));

    /// <summary>The key of a workspace's lead status of a category (0 Open, 1 Qualified, 2 Disqualified, 3 Converted).</summary>
    public static async Task<string> LeadStatusKeyAsync(Guid tenantId, int category)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(
            "SELECT TOP 1 [Key] FROM dbo.LeadStatusDefinitions WHERE TenantId = @t AND Category = @c ORDER BY IsActive DESC, SortOrder",
            conn);
        cmd.Parameters.AddWithValue("@t", tenantId);
        cmd.Parameters.AddWithValue("@c", category);
        return (string?)await cmd.ExecuteScalarAsync()
               ?? throw new InvalidOperationException($"The workspace has no lead status of category {category}.");
    }

    /// <summary>The currency stored on a record (Leads, Deals, Quotes, Invoices).</summary>
    public static async Task<string?> CurrencyAsync(string table, Guid id)
    {
        if (table is not ("Leads" or "Deals" or "Quotes" or "Invoices")) throw new ArgumentOutOfRangeException(nameof(table));
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand($"SELECT Currency FROM dbo.[{table}] WHERE Id = @id", conn);
        cmd.Parameters.AddWithValue("@id", id);
        return await cmd.ExecuteScalarAsync() as string;
    }
}
