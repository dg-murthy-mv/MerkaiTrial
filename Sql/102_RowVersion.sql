/* =====================================================================
   102_RowVersion.sql — two people editing the same record

   Adds a RowVersion column (SQL Server "rowversion") to
       dbo.Leads, dbo.Deals, dbo.Contacts, dbo.Companies

   SQL Server sets and changes it by itself on every write to the row;
   nothing in the app ever writes it. The app reads it with the record,
   the edit form keeps it in a hidden field, and a save made from an older
   value is refused ("Someone else changed this lead…") instead of
   silently overwriting the other person's change.

   Existing rows get a value the moment the column is added. Nothing else
   changes, and no data is touched.

   THE APP NEEDS THIS COLUMN as soon as this round's code is running:
   EF reads and checks it on these four tables. Run it BEFORE starting the
   new build, on both databases.

   ─────────────────────────────────────────────────────────────────────
   RUN IT ON BOTH DATABASES — your development database AND
   MerkaiTrial_E2E. Check the database dropdown in the toolbar first.
   ─────────────────────────────────────────────────────────────────────

   HOW TO RUN
     1. Run as-is (@Commit = 0) — rolled back. Grid 1 says what it would do.
     2. Set @Commit = 1 and run again. Messages end with COMMITTED; the last
        grid says OK.
     3. Switch the dropdown to the other database and do 1–2 again.

   SAFE TO RE-RUN — a table that already has the column is skipped.
   On a large table, adding the column takes a moment (every row is given
   its first value).
   ===================================================================== */

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit BIT = 0;      -- flip to 1 to commit

DECLARE @Tables TABLE (Name SYSNAME);
INSERT @Tables VALUES (N'Leads'), (N'Deals'), (N'Contacts'), (N'Companies');

IF EXISTS (SELECT 1 FROM @Tables t WHERE OBJECT_ID(N'dbo.' + t.Name, N'U') IS NULL)
BEGIN
    RAISERROR('One of dbo.Leads / Deals / Contacts / Companies is missing — is this the Merkai database?', 16, 1);
    RETURN;
END;

SELECT DB_NAME() AS [Database], t.Name AS [Table],
       CASE WHEN COL_LENGTH(N'dbo.' + t.Name, N'RowVersion') IS NULL
            THEN N'→ RowVersion will be added' ELSE N'already has RowVersion' END AS [Change]
FROM @Tables t;

BEGIN TRY
    BEGIN TRANSACTION;

    DECLARE @name SYSNAME, @sql NVARCHAR(MAX);
    DECLARE c CURSOR LOCAL FAST_FORWARD FOR SELECT Name FROM @Tables;
    OPEN c; FETCH NEXT FROM c INTO @name;
    WHILE @@FETCH_STATUS = 0
    BEGIN
        IF COL_LENGTH(N'dbo.' + @name, N'RowVersion') IS NULL
        BEGIN
            SET @sql = N'ALTER TABLE dbo.' + QUOTENAME(@name) + N' ADD RowVersion rowversion NOT NULL;';
            PRINT @sql;
            EXEC sys.sp_executesql @sql;
        END;
        FETCH NEXT FROM c INTO @name;
    END;
    CLOSE c; DEALLOCATE c;

    SELECT DB_NAME() AS [Database],
           CASE WHEN EXISTS (
                    SELECT 1 FROM @Tables t
                    WHERE NOT EXISTS (
                        SELECT 1 FROM sys.columns col
                        JOIN sys.types ty ON ty.user_type_id = col.user_type_id
                        WHERE col.object_id = OBJECT_ID(N'dbo.' + t.Name)
                          AND col.name = N'RowVersion'
                          AND ty.name = N'timestamp'))      -- rowversion's catalog name
                THEN N'PROBLEM — a table has no rowversion column'
                ELSE N'OK' END AS [Result];

    IF @Commit = 1
    BEGIN
        COMMIT TRANSACTION;
        PRINT 'COMMITTED on ' + DB_NAME() + '.';
    END
    ELSE
    BEGIN
        ROLLBACK TRANSACTION;
        PRINT 'ROLLED BACK (dry run). Set @Commit = 1 to apply.';
    END;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT 'FAILED, nothing was changed: ' + ERROR_MESSAGE();
    THROW;
END CATCH;
