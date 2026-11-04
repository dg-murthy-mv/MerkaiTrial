// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Infrastructure/PlanData.cs
//
// NEW (114). Puts a test workspace on a given plan, and sets its storage
// limit — straight in the database, the same three things the super
// admin's "change plan" writes (Tenants.Plan, TenantSettings.FeatureFlags,
// TenantSettings.StorageLimit). Limits on people, leads and deals are left
// alone, so nothing else a test does runs into them.
//
// Only ever the test database (_E2E), and only workspaces the tests built.
// =====================================================================

using Microsoft.Data.SqlClient;

namespace MerkaiTrial.E2E.Infrastructure;

public static class PlanData
{
    private static async Task<SqlConnection> OpenAsync()
    {
        var conn = new SqlConnection(E2ESettings.ConnectionString);
        await conn.OpenAsync();
        await using var check = new SqlCommand("SELECT DB_NAME()", conn);
        var name = (string?)await check.ExecuteScalarAsync() ?? "";
        if (!name.EndsWith("_E2E", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"PlanData only writes to an _E2E database, not '{name}'.");
        return conn;
    }

    /// <summary>The workspace's plan becomes <paramref name="planName"/>: its name, features and storage limit.</summary>
    public static async Task MoveToPlanAsync(Guid tenantId, string planName)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand(@"
            DECLARE @features nvarchar(max), @storage bigint;
            SELECT @features = Features, @storage = StorageLimitBytes FROM dbo.Plans WHERE Name = @plan AND IsActive = 1;
            IF @features IS NULL THROW 50000, 'There is no active plan with that name.', 1;

            UPDATE dbo.Tenants SET [Plan] = @plan WHERE Id = @t;
            UPDATE dbo.TenantSettings SET FeatureFlags = @features, StorageLimit = @storage WHERE TenantId = @t;
            IF @@ROWCOUNT = 0 THROW 50000, 'The workspace has no TenantSettings row.', 1;", conn);
        cmd.Parameters.AddWithValue("@plan", planName);
        cmd.Parameters.AddWithValue("@t", tenantId);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>The workspace's storage limit in bytes.</summary>
    public static async Task<long> StorageLimitAsync(Guid tenantId)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand("SELECT StorageLimit FROM dbo.TenantSettings WHERE TenantId = @t", conn);
        cmd.Parameters.AddWithValue("@t", tenantId);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync()
                               ?? throw new InvalidOperationException("The workspace has no TenantSettings row."));
    }

    /// <summary>Sets the workspace's storage limit in bytes.</summary>
    public static async Task SetStorageLimitAsync(Guid tenantId, long bytes)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new SqlCommand("UPDATE dbo.TenantSettings SET StorageLimit = @b WHERE TenantId = @t", conn);
        cmd.Parameters.AddWithValue("@b", bytes);
        cmd.Parameters.AddWithValue("@t", tenantId);
        if (await cmd.ExecuteNonQueryAsync() == 0)
            throw new InvalidOperationException("The workspace has no TenantSettings row.");
    }
}
