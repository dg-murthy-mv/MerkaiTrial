// =====================================================================
// FILE: MerkaiTrial.Infrastructure/Persistence/SchemaCheck.cs
//
// NEW (101). At startup, compares what the APP's model expects of every
// table and column with what the DATABASE actually has, and writes every
// difference to the log. Read-only: it changes nothing.
//
// WHY
//   Round 101 found two bugs that were never in the C# at all — the
//   database simply did not match it:
//     - Invoices.QuoteId was NOT NULL in the table, optional in the app,
//       so every manual invoice failed to save ("Failed to create invoice").
//     - Invoices.Status defaulted to 1 (Sent) in the table, 0 (Draft) in
//       the app, so every new invoice came out already Sent.
//   Both hid for many rounds because nothing compared the two. Tables are
//   created by hand-written SQL scripts, not EF migrations, so drift like
//   this can come back with any script. This check makes it visible on
//   every start, in webapi.out.log, before a user or a test trips on it.
//
// WHAT IT REPORTS (one log line each, prefixed [SchemaCheck])
//   MISSING TABLE     the app maps an entity to a table that isn't there
//   MISSING COLUMN    the app reads/writes a column that isn't there
//   NOT NULL IN DB    the app may save it empty; the database refuses
//                     (the QuoteId bug)
//   NULL IN DB        the app treats it as always present; one NULL row
//                     and reading it crashes (SqlNullValueException)
//   TOO SHORT IN DB   the app allows longer text than the column holds
//                     ("String or binary data would be truncated")
//   DECIMAL SHAPE     different precision/scale — values are rounded on
//                     the way in (the 052/062 Quantity bug)
//   DEFAULT DIFFERS   for status / yes-no / number columns with a fixed
//                     default: the table's DEFAULT is not the app's. The
//                     app always sends its value since 101
//                     (FlowDbContext.ApplyAlwaysSendValues), so this only
//                     matters for hand-written INSERTs — reported as a
//                     note, not a problem.
//   Then one summary line: "N problem(s)" or "the database matches".
//
// It never stops the app starting; any failure of the check itself is
// logged as a warning and skipped.
// =====================================================================

using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MerkaiTrial.Infrastructure.Persistence
{
    public static class SchemaCheck
    {
        private sealed record DbColumn(
            bool Nullable, string Type, int CharLength, byte Precision, int Scale, string? Default);

        /// <summary>Runs the check and logs the result. Returns the number of problems, or -1 if it could not run.</summary>
        public static async Task<int> RunAsync(IServiceProvider services, string hostName, CancellationToken ct = default)
        {
            var logger = services.GetService<ILoggerFactory>()?.CreateLogger("SchemaCheck");
            try
            {
                using var scope = services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FlowDbContext>();

                if (!await db.Database.CanConnectAsync(ct))
                {
                    logger?.LogWarning("[SchemaCheck] [{Host}] database not reachable — check skipped.", hostName);
                    return -1;
                }

                var (tables, columns) = await ReadDatabaseAsync(db, ct);
                var (problems, notes) = Compare(db.Model, tables, columns);

                foreach (var n in notes)
                    logger?.LogInformation("[SchemaCheck] note: {Note}", n);
                foreach (var p in problems)
                    logger?.LogWarning("[SchemaCheck] {Problem}", p);

                if (problems.Count == 0)
                    logger?.LogInformation("[SchemaCheck] [{Host}] the database matches the app's model ({Notes} note(s)).",
                        hostName, notes.Count);
                else
                    logger?.LogWarning("[SchemaCheck] [{Host}] {Count} problem(s) — the database does not match the app. " +
                                       "Each line above names the table and column.", hostName, problems.Count);

                return problems.Count;
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "[SchemaCheck] [{Host}] the check itself failed — skipped.", hostName);
                return -1;
            }
        }

        // ── The database's own description of itself ─────────────────────
        private static async Task<(HashSet<string> Tables, Dictionary<string, DbColumn> Columns)> ReadDatabaseAsync(
            FlowDbContext db, CancellationToken ct)
        {
            var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var columns = new Dictionary<string, DbColumn>(StringComparer.OrdinalIgnoreCase);

            var conn = db.Database.GetDbConnection();
            var opened = false;
            if (conn.State != ConnectionState.Open)
            {
                await conn.OpenAsync(ct);
                opened = true;
            }

            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
SELECT s.name, t.name, c.name, c.is_nullable, ty.name, c.max_length, c.precision, c.scale, dc.definition
FROM sys.columns c
JOIN sys.tables  t  ON t.object_id = c.object_id
JOIN sys.schemas s  ON s.schema_id = t.schema_id
JOIN sys.types   ty ON ty.user_type_id = c.user_type_id
LEFT JOIN sys.default_constraints dc
       ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id";

                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    var schema = r.GetString(0);
                    var table = r.GetString(1);
                    var type = r.GetString(4);
                    var maxLength = Convert.ToInt32(r.GetValue(5), CultureInfo.InvariantCulture);
                    var charLength = maxLength == -1 ? -1
                        : type is "nvarchar" or "nchar" ? maxLength / 2
                        : maxLength;

                    tables.Add(Key(schema, table));
                    columns[Key(schema, table, r.GetString(2))] = new DbColumn(
                        Nullable: r.GetBoolean(3),
                        Type: type,
                        CharLength: charLength,
                        Precision: r.GetByte(6),
                        Scale: Convert.ToInt32(r.GetValue(7), CultureInfo.InvariantCulture),
                        Default: r.IsDBNull(8) ? null : r.GetString(8));
                }
            }
            finally
            {
                if (opened) await conn.CloseAsync();
            }

            return (tables, columns);
        }

        // ── App model vs database ────────────────────────────────────────
        private static (List<string> Problems, List<string> Notes) Compare(
            IModel model, HashSet<string> tables, Dictionary<string, DbColumn> columns)
        {
            var problems = new List<string>();
            var notes = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // shared tables: say it once
            var defaultSchema = model.GetDefaultSchema() ?? "dbo";

            foreach (var entity in model.GetEntityTypes().OrderBy(e => e.GetTableName()))
            {
                var table = entity.GetTableName();
                if (table is null) continue;                       // a view, a raw query, or not mapped
                var schema = entity.GetSchema() ?? defaultSchema;
                var store = StoreObjectIdentifier.Table(table, schema);
                var where = schema.Equals("dbo", StringComparison.OrdinalIgnoreCase) ? table : $"{schema}.{table}";

                if (!tables.Contains(Key(schema, table)))
                {
                    if (seen.Add("T|" + where))
                        problems.Add($"MISSING TABLE {where} — the app uses it ({entity.ClrType.Name}); the database has no such table.");
                    continue;
                }

                foreach (var p in entity.GetProperties())
                {
                    var column = p.GetColumnName(store);
                    if (column is null) continue;
                    var at = $"{where}.{column}";
                    if (!seen.Add("C|" + at)) continue;

                    if (!columns.TryGetValue(Key(schema, table, column), out var db))
                    {
                        problems.Add($"MISSING COLUMN {at} — the app reads and writes it ({entity.ClrType.Name}.{p.Name}); the table has no such column.");
                        continue;
                    }

                    var appNullable = p.IsColumnNullable(store);
                    if (appNullable && !db.Nullable)
                        problems.Add($"NOT NULL IN DB {at} — the app may save it empty ({entity.ClrType.Name}.{p.Name} is optional), " +
                                     "but the column refuses NULL, so that save fails.");
                    else if (!appNullable && db.Nullable && !p.IsPrimaryKey())
                        problems.Add($"NULL IN DB {at} — the app expects a value every time ({entity.ClrType.Name}.{p.Name}), " +
                                     "but the column allows NULL; a row with NULL there will crash when it is read.");

                    var maxLength = p.GetMaxLength();
                    if (maxLength is int appLength && db.CharLength > 0 && db.CharLength < appLength
                        && db.Type is "nvarchar" or "varchar" or "nchar" or "char")
                        problems.Add($"TOO SHORT IN DB {at} — the app allows {appLength} characters, the column holds {db.CharLength}; " +
                                     "longer text fails to save.");

                    var clr = Nullable.GetUnderlyingType(p.ClrType) ?? p.ClrType;
                    if (clr == typeof(decimal) && db.Type is "decimal" or "numeric")
                    {
                        var precision = p.GetPrecision();
                        var scale = p.GetScale();
                        if (precision is int ap && (ap != db.Precision || (scale ?? 0) != db.Scale))
                            problems.Add($"DECIMAL SHAPE {at} — the app sends DECIMAL({ap},{scale ?? 0}), the column is " +
                                         $"DECIMAL({db.Precision},{db.Scale}); values are rounded or refused.");
                    }

                    var appDefault = p.GetDefaultValue();
                    if (appDefault is not null && p.GetDefaultValueSql() is null && clr.IsValueType)
                    {
                        var appSql = SqlLiteral(appDefault);
                        if (appSql is not null && !SameDefault(db.Default, appSql))
                            notes.Add($"DEFAULT DIFFERS {at} — the app's default is {appSql}, the table's is " +
                                      $"{db.Default ?? "(none)"}. Harmless for the app (it always sends the value); " +
                                      "a hand-written INSERT would get the table's.");
                    }
                }
            }

            return (problems, notes);
        }

        // ── helpers ──────────────────────────────────────────────────────
        private static string Key(string schema, string table) => $"{schema}.{table}";
        private static string Key(string schema, string table, string column) => $"{schema}.{table}.{column}";

        /// <summary>The app's default as SQL would write it, or null when it isn't a plain number or yes/no.</summary>
        private static string? SqlLiteral(object value) => value switch
        {
            bool b => b ? "1" : "0",
            Enum e => Convert.ToInt64(e, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            byte or short or int or long => Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture),
            decimal d => d.ToString(CultureInfo.InvariantCulture),
            double d => d.ToString(CultureInfo.InvariantCulture),
            _ => null,
        };

        /// <summary>"((0))", "(0)", "((0.00))" and "0" all count as the same default.</summary>
        private static bool SameDefault(string? dbDefault, string appSql)
        {
            if (dbDefault is null) return false;
            var bare = dbDefault.Trim().Trim('(', ')').Trim('\'', 'N');
            if (decimal.TryParse(bare, NumberStyles.Number, CultureInfo.InvariantCulture, out var a)
                && decimal.TryParse(appSql, NumberStyles.Number, CultureInfo.InvariantCulture, out var b))
                return a == b;
            return string.Equals(bare, appSql, StringComparison.OrdinalIgnoreCase);
        }
    }
}
