// =====================================================================
// FILE: tests/MerkaiTrial.E2E/Infrastructure/DbSnapshot.cs
//
// NEW (110). A picture of a database, to compare before and after the
// Sql scripts run (SqlScriptRerunTests). Three parts:
//
//   Schema — one line per thing, so two pictures can be compared line by
//            line:
//              • every column: type, size, NULL or not, identity,
//                computed and default definitions, collation
//              • every index: kind, unique/primary key, key columns in
//                order (and direction), included columns, filter, and
//                DISABLED if it is
//              • every foreign key and check constraint, with
//                "not trusted" / "disabled" when SQL Server isn't
//                enforcing it
//              • every view, procedure, function and trigger (a hash of
//                its text) and whether a trigger is disabled
//              • sequences, user types, and the database's snapshot /
//                compatibility settings
//            Names SQL Server makes up (DF__Leads__Statu__3B75D760) are
//            left out where the definition says the same thing.
//   Data   — for every table: its row count and a checksum of all its
//            rows. If a second run of the scripts changes a single value
//            anywhere, the checksum changes.
//   Broken — views, procedures and functions SQL Server can no longer
//            compile (sp_refreshsqlmodule fails: a column they use is
//            gone, a type changed).
// =====================================================================

using System.Text;
using Microsoft.Data.SqlClient;

namespace MerkaiTrial.E2E.Infrastructure;

public sealed class DbSnapshot
{
    public SortedSet<string> Schema { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, string> Data { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, string> Broken { get; } = new(StringComparer.Ordinal);

    public static async Task<DbSnapshot> TakeAsync(string connectionString)
    {
        var s = new DbSnapshot();
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();

        await ColumnsAsync(conn, s.Schema);
        await IndexesAsync(conn, s.Schema);
        await ForeignKeysAsync(conn, s.Schema);
        await LinesAsync(conn, s.Schema, CheckSql);
        await LinesAsync(conn, s.Schema, ModulesSql);
        await LinesAsync(conn, s.Schema, SequencesSql);
        await LinesAsync(conn, s.Schema, TypesSql);
        await LinesAsync(conn, s.Schema, DatabaseSql);
        await DataAsync(conn, s.Data);
        await BrokenAsync(conn, s.Broken);
        return s;
    }

    // ── comparing ─────────────────────────────────────────────────────

    /// <summary>"+ line" for what is new in <paramref name="after"/>, "- line" for what went.</summary>
    public static List<string> DiffSchema(DbSnapshot before, DbSnapshot after) =>
        after.Schema.Except(before.Schema).Select(l => "+ " + l)
            .Concat(before.Schema.Except(after.Schema).Select(l => "- " + l))
            .OrderBy(l => l[2..], StringComparer.Ordinal).ThenBy(l => l[0])
            .ToList();

    /// <summary>Tables whose rows changed, as "table: rows=1 sum=2 → rows=1 sum=3".</summary>
    public static List<string> DiffData(DbSnapshot before, DbSnapshot after)
    {
        var list = new List<string>();
        foreach (var table in before.Data.Keys.Union(after.Data.Keys).OrderBy(k => k, StringComparer.Ordinal))
        {
            before.Data.TryGetValue(table, out var b);
            after.Data.TryGetValue(table, out var a);
            if (!string.Equals(a, b, StringComparison.Ordinal))
                list.Add($"{table}: {b ?? "(no table)"} → {a ?? "(no table)"}");
        }
        return list;
    }

    /// <summary>Objects broken in <paramref name="after"/> that compiled in <paramref name="before"/>.</summary>
    public static List<string> NewlyBroken(DbSnapshot before, DbSnapshot after) =>
        after.Broken.Where(kv => !before.Broken.ContainsKey(kv.Key)).Select(kv => $"{kv.Key}: {kv.Value}").ToList();

    /// <summary>Schema lines in <paramref name="after"/> flagged DISABLED / not trusted that weren't in <paramref name="before"/>.</summary>
    public static List<string> NewlyUnenforced(DbSnapshot before, DbSnapshot after) =>
        after.Schema.Where(l => (l.Contains(" DISABLED", StringComparison.Ordinal) || l.Contains(" NOT-TRUSTED", StringComparison.Ordinal))
                                && !before.Schema.Contains(l))
            .ToList();

    // ── schema ────────────────────────────────────────────────────────

    private const string ColumnsSql = @"
SELECT s.name, t.name, c.name, TYPE_NAME(c.user_type_id), c.max_length, c.precision, c.scale,
       c.is_nullable, c.is_identity, cc.definition, dc.definition, c.collation_name
FROM sys.tables t
JOIN sys.schemas s ON s.schema_id = t.schema_id
JOIN sys.columns c ON c.object_id = t.object_id
LEFT JOIN sys.computed_columns cc ON cc.object_id = c.object_id AND cc.column_id = c.column_id
LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
WHERE t.is_ms_shipped = 0";

    private static async Task ColumnsAsync(SqlConnection conn, SortedSet<string> into)
    {
        await using var cmd = new SqlCommand(ColumnsSql, conn) { CommandTimeout = 300 };
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var sb = new StringBuilder()
                .Append($"column {r.GetString(0)}.{r.GetString(1)}.{r.GetString(2)} {r.GetString(3)}")
                .Append($"({r.GetInt16(4)},{r.GetByte(5)},{r.GetByte(6)})")
                .Append(r.GetBoolean(7) ? " null" : " not-null");
            if (r.GetBoolean(8)) sb.Append(" identity");
            if (!r.IsDBNull(9)) sb.Append(" computed:").Append(r.GetString(9));
            if (!r.IsDBNull(10)) sb.Append(" default:").Append(r.GetString(10));
            if (!r.IsDBNull(11)) sb.Append(" collate:").Append(r.GetString(11));
            into.Add(sb.ToString());
        }
    }

    private const string IndexSql = @"
SELECT i.object_id, i.index_id, s.name, t.name, i.name, i.type_desc, i.is_unique, i.is_primary_key,
       i.is_unique_constraint, i.is_disabled, i.filter_definition
FROM sys.indexes i
JOIN sys.tables t ON t.object_id = i.object_id
JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE i.type > 0 AND t.is_ms_shipped = 0";

    private const string IndexColumnsSql = @"
SELECT ic.object_id, ic.index_id, c.name, ic.key_ordinal, ic.is_included_column, ic.is_descending_key
FROM sys.index_columns ic
JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
JOIN sys.tables t ON t.object_id = ic.object_id
WHERE t.is_ms_shipped = 0
ORDER BY ic.object_id, ic.index_id, ic.key_ordinal, c.name";

    private static async Task IndexesAsync(SqlConnection conn, SortedSet<string> into)
    {
        var keys = new Dictionary<(int, int), List<string>>();
        var includes = new Dictionary<(int, int), List<string>>();
        await using (var cmd = new SqlCommand(IndexColumnsSql, conn) { CommandTimeout = 300 })
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
            {
                var id = (r.GetInt32(0), r.GetInt32(1));
                if (r.GetBoolean(4))
                    (includes.TryGetValue(id, out var inc) ? inc : includes[id] = new()).Add(r.GetString(2));
                else if (r.GetByte(3) > 0)
                    (keys.TryGetValue(id, out var k) ? k : keys[id] = new()).Add(r.GetString(2) + (r.GetBoolean(5) ? " desc" : ""));
            }
        }

        await using (var cmd = new SqlCommand(IndexSql, conn) { CommandTimeout = 300 })
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
            {
                var id = (r.GetInt32(0), r.GetInt32(1));
                var sb = new StringBuilder()
                    .Append($"index {r.GetString(2)}.{r.GetString(3)}.{(r.IsDBNull(4) ? "(heap)" : r.GetString(4))} {r.GetString(5)}");
                if (r.GetBoolean(7)) sb.Append(" primary-key");
                else if (r.GetBoolean(8)) sb.Append(" unique-constraint");
                else if (r.GetBoolean(6)) sb.Append(" unique");
                sb.Append(" keys(").Append(string.Join(",", keys.GetValueOrDefault(id) ?? new())).Append(')');
                var inc = includes.GetValueOrDefault(id);
                if (inc is { Count: > 0 }) sb.Append(" include(").Append(string.Join(",", inc.OrderBy(x => x, StringComparer.Ordinal))).Append(')');
                if (!r.IsDBNull(10)) sb.Append(" where:").Append(r.GetString(10));
                if (r.GetBoolean(9)) sb.Append(" DISABLED");
                into.Add(sb.ToString());
            }
        }
    }

    private const string ForeignKeySql = @"
SELECT fk.object_id, fk.name, fk.is_system_named, ps.name, pt.name, rs.name, rt.name,
       fk.delete_referential_action_desc, fk.update_referential_action_desc, fk.is_disabled, fk.is_not_trusted
FROM sys.foreign_keys fk
JOIN sys.tables pt ON pt.object_id = fk.parent_object_id
JOIN sys.schemas ps ON ps.schema_id = pt.schema_id
JOIN sys.tables rt ON rt.object_id = fk.referenced_object_id
JOIN sys.schemas rs ON rs.schema_id = rt.schema_id
WHERE pt.is_ms_shipped = 0";

    private const string ForeignKeyColumnsSql = @"
SELECT fkc.constraint_object_id, pc.name, rc.name
FROM sys.foreign_key_columns fkc
JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
ORDER BY fkc.constraint_object_id, fkc.constraint_column_id";

    private static async Task ForeignKeysAsync(SqlConnection conn, SortedSet<string> into)
    {
        var cols = new Dictionary<int, (List<string> From, List<string> To)>();
        await using (var cmd = new SqlCommand(ForeignKeyColumnsSql, conn) { CommandTimeout = 300 })
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
            {
                var id = r.GetInt32(0);
                if (!cols.TryGetValue(id, out var c)) cols[id] = c = (new(), new());
                c.From.Add(r.GetString(1));
                c.To.Add(r.GetString(2));
            }
        }

        await using (var cmd = new SqlCommand(ForeignKeySql, conn) { CommandTimeout = 300 })
        await using (var r = await cmd.ExecuteReaderAsync())
        {
            while (await r.ReadAsync())
            {
                var c = cols.GetValueOrDefault(r.GetInt32(0));
                var name = r.GetBoolean(2) ? "" : r.GetString(1) + " ";
                var sb = new StringBuilder()
                    .Append($"foreign-key {name}{r.GetString(3)}.{r.GetString(4)}({string.Join(",", c.From ?? new())})")
                    .Append($" -> {r.GetString(5)}.{r.GetString(6)}({string.Join(",", c.To ?? new())})")
                    .Append($" on-delete:{r.GetString(7)} on-update:{r.GetString(8)}");
                if (r.GetBoolean(9)) sb.Append(" DISABLED");
                if (r.GetBoolean(10)) sb.Append(" NOT-TRUSTED");
                into.Add(sb.ToString());
            }
        }
    }

    // 110-update: each piece is its own column and the line is put together
    // here. Joining them in SQL ("'check ' + s.name + …") fails on a server
    // whose catalog names use a different collation from the database
    // ("Cannot resolve collation conflict … in add operator").

    private const string CheckSql = @"
SELECT 'check', s.name, t.name,
       CASE WHEN cc.is_system_named = 1 THEN NULL ELSE cc.name END,
       cc.definition,
       CASE WHEN cc.is_disabled = 1 THEN 'DISABLED' END,
       CASE WHEN cc.is_not_trusted = 1 THEN 'NOT-TRUSTED' END
FROM sys.check_constraints cc
JOIN sys.tables t ON t.object_id = cc.parent_object_id
JOIN sys.schemas s ON s.schema_id = t.schema_id
WHERE t.is_ms_shipped = 0";

    private const string ModulesSql = @"
SELECT LOWER(o.type_desc), s.name, o.name,
       'sha256:' , CONVERT(varchar(64), HASHBYTES('SHA2_256', CONVERT(nvarchar(max), m.definition)), 2),
       CASE WHEN tr.is_disabled = 1 THEN 'DISABLED' END
FROM sys.sql_modules m
JOIN sys.objects o ON o.object_id = m.object_id
JOIN sys.schemas s ON s.schema_id = o.schema_id
LEFT JOIN sys.triggers tr ON tr.object_id = o.object_id
WHERE o.is_ms_shipped = 0";

    private const string SequencesSql = @"
SELECT 'sequence', s.name, q.name, TYPE_NAME(q.user_type_id),
       'start:', CONVERT(varchar(40), q.start_value), 'step:', CONVERT(varchar(40), q.increment),
       'min:', CONVERT(varchar(40), q.minimum_value), 'max:', CONVERT(varchar(40), q.maximum_value),
       CASE WHEN q.is_cycling = 1 THEN 'cycle' END
FROM sys.sequences q JOIN sys.schemas s ON s.schema_id = q.schema_id";

    private const string TypesSql = @"
SELECT 'type', s.name, ty.name, TYPE_NAME(ty.system_type_id), CONVERT(varchar(10), ty.max_length),
       CASE WHEN ty.is_nullable = 1 THEN 'null' ELSE 'not-null' END,
       CASE WHEN ty.is_table_type = 1 THEN 'table-type' END
FROM sys.types ty JOIN sys.schemas s ON s.schema_id = ty.schema_id
WHERE ty.is_user_defined = 1";

    private const string DatabaseSql = @"
SELECT 'database', 'compatibility:', CONVERT(varchar(10), compatibility_level),
       'read-committed-snapshot:', CONVERT(varchar(1), is_read_committed_snapshot_on),
       'snapshot-isolation:', snapshot_isolation_state_desc
FROM sys.databases WHERE database_id = DB_ID()";

    /// <summary>One line per row: its non-empty columns, separated by spaces.</summary>
    private static async Task LinesAsync(SqlConnection conn, SortedSet<string> into, string sql)
    {
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 300 };
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var parts = new List<string>();
            for (var i = 0; i < r.FieldCount; i++)
            {
                if (r.IsDBNull(i)) continue;
                var v = Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture);
                if (!string.IsNullOrEmpty(v)) parts.Add(v);
            }
            into.Add(string.Join(" ", parts).Replace(": ", ":"));
        }
    }

    // ── data ──────────────────────────────────────────────────────────

    private static async Task DataAsync(SqlConnection conn, SortedDictionary<string, string> into)
    {
        var tables = new List<string>();
        await using (var cmd = new SqlCommand(
            "SELECT QUOTENAME(s.name) + '.' + QUOTENAME(t.name) FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id " +
            "WHERE t.is_ms_shipped = 0 ORDER BY 1", conn))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) tables.Add(r.GetString(0));

        foreach (var table in tables)
        {
            try
            {
                await using var cmd = new SqlCommand(
                    $"SELECT COUNT_BIG(*), CHECKSUM_AGG(BINARY_CHECKSUM(*)) FROM {table}", conn) { CommandTimeout = 0 };
                await using var r = await cmd.ExecuteReaderAsync();
                await r.ReadAsync();
                into[table] = $"rows={r.GetInt64(0)} checksum={(r.IsDBNull(1) ? "none" : r.GetInt32(1).ToString())}";
            }
            catch (SqlException ex)
            {
                into[table] = "unreadable: " + ex.Message;
            }
        }
    }

    // ── what no longer compiles ───────────────────────────────────────

    private static async Task BrokenAsync(SqlConnection conn, SortedDictionary<string, string> into)
    {
        var modules = new List<string>();
        await using (var cmd = new SqlCommand(@"
SELECT QUOTENAME(s.name) + '.' + QUOTENAME(o.name)
FROM sys.sql_modules m
JOIN sys.objects o ON o.object_id = m.object_id
JOIN sys.schemas s ON s.schema_id = o.schema_id
WHERE o.is_ms_shipped = 0 AND m.is_schema_bound = 0 AND o.type IN ('V', 'P', 'FN', 'IF', 'TF')
ORDER BY 1", conn))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) modules.Add(r.GetString(0));

        foreach (var module in modules)
        {
            try
            {
                await using var cmd = new SqlCommand("EXEC sys.sp_refreshsqlmodule @name", conn) { CommandTimeout = 120 };
                cmd.Parameters.AddWithValue("@name", module);
                await cmd.ExecuteNonQueryAsync();
            }
            catch (SqlException ex)
            {
                into[module] = ex.Message;
            }
        }
    }
}
