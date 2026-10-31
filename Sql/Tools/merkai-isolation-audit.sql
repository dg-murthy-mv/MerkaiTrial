/* =====================================================================
   MerkaiTrial — TENANT ISOLATION AUDIT
   Read-only. Run each query separately (or add GO between them).

   This drives the global query filter work. Do not skip query 3:
   child tables WITHOUT a TenantId column cannot be filtered directly,
   and how we protect those is the main design decision in phase 1.
   ===================================================================== */


/* ---------------------------------------------------------------------
   1. WHICH TABLES CARRY TenantId, AND CAN THEY BE QUERIED EFFICIENTLY

   Tables with NO TenantId sort first — those are the design problem.

   The index column matters as much as the presence of TenantId: a
   global filter adds "WHERE TenantId = @p" to EVERY query on that
   table. Without an index leading on TenantId that becomes a full scan
   on every page load once several tenants share the table.

   A NULLABLE TenantId on a tenant-scoped table is its own bug:
   "WHERE TenantId = @x" silently excludes NULL rows, so orphaned
   records become invisible rather than erroring.
   (Roles.TenantId is nullable BY DESIGN — NULL means system role.)
   --------------------------------------------------------------------- */
SELECT
    t.name AS TableName,
    CASE WHEN tc.name IS NULL THEN 'NO TenantId' ELSE 'has TenantId' END AS TenantColumn,
    CASE WHEN tc.is_nullable = 1 THEN 'NULLABLE' ELSE '' END AS Nullability,
    ISNULL((
        SELECT STRING_AGG(i.name, ', ')
        FROM sys.indexes i
        JOIN sys.index_columns ic
              ON ic.object_id = i.object_id
             AND ic.index_id  = i.index_id
             AND ic.key_ordinal = 1
        WHERE i.object_id = t.object_id
          AND ic.column_id = tc.column_id
    ), '(none)') AS IndexLeadingWithTenantId,
    (SELECT SUM(p.[rows]) FROM sys.partitions p
      WHERE p.object_id = t.object_id AND p.index_id IN (0,1)) AS ApproxRows
FROM sys.tables t
LEFT JOIN sys.columns tc
       ON tc.object_id = t.object_id AND tc.name = 'TenantId'
WHERE t.is_ms_shipped = 0
ORDER BY
    CASE WHEN tc.name IS NULL THEN 0 ELSE 1 END,
    t.name;


/* ---------------------------------------------------------------------
   2. IS Deals.Currency A REAL COLUMN?

   DealConfiguration says "Currency REMOVED — column dropped", but
   Deal.cs still declares `public string Currency`. There is no
   .Ignore() for it, so EF maps it by convention. If the column really
   were gone, every Deal query would throw — and yours do not, so the
   comment is probably stale. Confirming, because a stale comment in a
   configuration file is how the next person removes a live column.
   --------------------------------------------------------------------- */
SELECT
    CASE WHEN COL_LENGTH('dbo.Deals','Currency') IS NULL
         THEN 'MISSING — the entity property must be Ignore()d or EF will throw'
         ELSE 'EXISTS — the DealConfiguration comment is stale, fix the comment'
    END AS DealsCurrencyColumn;


/* ---------------------------------------------------------------------
   3. CHILD TABLES WITH NO TenantId

   These are reachable only through a parent (QuoteItems via Quotes,
   InvoiceLines via Invoices, DealNotes via Deals, and so on).

   EF Core cannot apply a tenant filter to them directly. Two options,
   and the row counts below decide which is worth it:

     a. ADD TenantId to each child table and filter it like everything
        else. More columns and a backfill migration, but every query is
        protected regardless of how it was written.

     b. Rely on always reaching them through a filtered parent. No
        schema change, but a single handler that queries QuoteItems
        directly by Id bypasses isolation entirely — and nothing warns
        you.

   For a system holding two clients' commercial data I would take (a)
   for anything carrying real content — QuoteItems, InvoiceLines,
   Payments, notes and activities. Pure join tables like UserRoles can
   stay as (b) since they have no content of their own.
   --------------------------------------------------------------------- */
SELECT
    t.name AS TableName,
    (SELECT SUM(p.[rows]) FROM sys.partitions p
      WHERE p.object_id = t.object_id AND p.index_id IN (0,1)) AS ApproxRows,
    ISNULL((
        SELECT STRING_AGG(cp.name, ', ')
        FROM sys.foreign_keys fk
        JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
        JOIN sys.columns cp ON cp.object_id = fk.parent_object_id
                           AND cp.column_id = fkc.parent_column_id
        WHERE fk.parent_object_id = t.object_id
    ), '(no FKs)') AS ReachedVia
FROM sys.tables t
WHERE t.is_ms_shipped = 0
  AND NOT EXISTS (SELECT 1 FROM sys.columns c
                   WHERE c.object_id = t.object_id AND c.name = 'TenantId')
ORDER BY t.name;


/* ---------------------------------------------------------------------
   4. ATTACHMENTS — what the download endpoint has to serve

   File downloads are currently BROKEN: the static-file middleware that
   served /Uploads was removed (correctly — it ran before authentication
   and performed no tenant check), and no replacement endpoint exists.
   Every stored FileUrl now 404s.

   The new endpoint should resolve by Attachment.Id and stream the file
   after checking TenantId — never by trusting a path from the URL. This
   shows the shape of the existing FileUrl values so old links can be
   redirected rather than orphaned.
   --------------------------------------------------------------------- */
SELECT TOP 20
    a.Id, a.TenantId, a.EntityType, a.FileName, a.FileUrl, a.MimeType, a.IsDeleted
FROM dbo.Attachments a
ORDER BY a.CreatedAtUtc DESC;

SELECT COUNT(*) AS TotalAttachments,
       SUM(CASE WHEN IsDeleted = 1 THEN 1 ELSE 0 END) AS DeletedAttachments
FROM dbo.Attachments;

/* Is there an index supporting the polymorphic lookup? The download and
   list paths both search on (TenantId, EntityType, EntityId). */
SELECT i.name AS IndexName, i.is_unique,
       STRING_AGG(c.name, ', ') WITHIN GROUP (ORDER BY ic.key_ordinal) AS KeyColumns
FROM sys.indexes i
JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
JOIN sys.columns c ON c.object_id = i.object_id AND c.column_id = ic.column_id
WHERE i.object_id = OBJECT_ID('dbo.Attachments') AND i.type > 0
GROUP BY i.name, i.is_unique;
