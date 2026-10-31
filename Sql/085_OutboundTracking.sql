/* =====================================================================
   085_OutboundTracking.sql — emailing invoices, and knowing what happened
                              to every email after it left

   Adds:
     dbo.OutboundMessages
         CcAddresses          NVARCHAR(1700) NULL   copies (invoice emails)
         SentByUserId         UNIQUEIDENTIFIER NULL who pressed Send — told on a bounce
         DeliveryStatus       INT NOT NULL DEFAULT 0
                              0 None, 1 Delivered, 2 Delayed, 3 Bounced,
                              4 Complained (marked as spam), 5 Failed
         DeliveryStatusAtUtc  DATETIME2 NULL
         DeliveryDetail       NVARCHAR(500) NULL    the bounce reason
         IX_OutboundMessages_Provider              Resend's webhook names an
                                                   email only by its own id

     dbo.OutboundMessageAttachments
         Files sent with an email — the invoice PDF. Bytes stored in the
         row, deleted with the message (ON DELETE CASCADE).

     dbo.Invoices.PublicLinkToken   NVARCHAR(64) NULL, unique where set
         The customer's link, /i/{token}. Made on the first email.

     dbo.InvoiceSentCopies
         The PDF as last emailed, one row per invoice, for the customer's
         "Download PDF" button.

   Nothing existing is changed or back-filled. Every existing email reads
   DeliveryStatus = 0 ("nothing heard"), which is the truth.

   HOW TO RUN
     1. Run as-is. @Commit = 0 — everything is ROLLED BACK. Grid 1: every
        row should say OK.
     2. Change @Commit to 1 and run again. Grid 1 again: all OK.

   SAFE TO RE-RUN. Every CREATE / ALTER is guarded.
   ===================================================================== */

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit BIT = 0;      -- flip to 1 to commit

BEGIN TRAN;

-- ── OutboundMessages: new columns ────────────────────────────────────
IF COL_LENGTH(N'dbo.OutboundMessages', N'CcAddresses') IS NULL
BEGIN
    EXEC sp_executesql N'ALTER TABLE dbo.OutboundMessages ADD CcAddresses NVARCHAR(1700) NULL;';
    PRINT 'Added OutboundMessages.CcAddresses';
END

IF COL_LENGTH(N'dbo.OutboundMessages', N'SentByUserId') IS NULL
BEGIN
    EXEC sp_executesql N'ALTER TABLE dbo.OutboundMessages ADD SentByUserId UNIQUEIDENTIFIER NULL;';
    PRINT 'Added OutboundMessages.SentByUserId';
END

IF COL_LENGTH(N'dbo.OutboundMessages', N'DeliveryStatus') IS NULL
BEGIN
    EXEC sp_executesql N'ALTER TABLE dbo.OutboundMessages ADD DeliveryStatus INT NOT NULL
                         CONSTRAINT DF_OutboundMessages_DeliveryStatus DEFAULT (0);';
    PRINT 'Added OutboundMessages.DeliveryStatus';
END

IF COL_LENGTH(N'dbo.OutboundMessages', N'DeliveryStatusAtUtc') IS NULL
BEGIN
    EXEC sp_executesql N'ALTER TABLE dbo.OutboundMessages ADD DeliveryStatusAtUtc DATETIME2 NULL;';
    PRINT 'Added OutboundMessages.DeliveryStatusAtUtc';
END

IF COL_LENGTH(N'dbo.OutboundMessages', N'DeliveryDetail') IS NULL
BEGIN
    EXEC sp_executesql N'ALTER TABLE dbo.OutboundMessages ADD DeliveryDetail NVARCHAR(500) NULL;';
    PRINT 'Added OutboundMessages.DeliveryDetail';
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_OutboundMessages_Provider' AND object_id = OBJECT_ID(N'dbo.OutboundMessages'))
BEGIN
    EXEC sp_executesql N'CREATE INDEX IX_OutboundMessages_Provider
                         ON dbo.OutboundMessages(ProviderMessageId) WHERE ProviderMessageId IS NOT NULL;';
    PRINT 'Created IX_OutboundMessages_Provider';
END

-- ── OutboundMessageAttachments ───────────────────────────────────────
IF OBJECT_ID(N'dbo.OutboundMessageAttachments', N'U') IS NULL
BEGIN
    EXEC sp_executesql N'
    CREATE TABLE dbo.OutboundMessageAttachments
    (
        Id                 UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_OutboundMessageAttachments PRIMARY KEY,
        OutboundMessageId  UNIQUEIDENTIFIER NOT NULL,
        TenantId           UNIQUEIDENTIFIER NOT NULL,
        FileName           NVARCHAR(255)    NOT NULL,
        ContentType        NVARCHAR(100)    NOT NULL,
        SizeBytes          BIGINT           NOT NULL,
        Content            VARBINARY(MAX)   NOT NULL,
        CreatedAtUtc       DATETIME2        NOT NULL CONSTRAINT DF_OutboundMessageAttachments_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT FK_OutboundMessageAttachments_Message FOREIGN KEY (OutboundMessageId)
            REFERENCES dbo.OutboundMessages(Id) ON DELETE CASCADE
    );';
    EXEC sp_executesql N'CREATE INDEX IX_OutboundMessageAttachments_Message
                         ON dbo.OutboundMessageAttachments(OutboundMessageId);';
    PRINT 'Created dbo.OutboundMessageAttachments';
END
ELSE
    PRINT 'dbo.OutboundMessageAttachments already exists — skipped';

-- ── Invoices.PublicLinkToken ─────────────────────────────────────────
IF COL_LENGTH(N'dbo.Invoices', N'PublicLinkToken') IS NULL
BEGIN
    EXEC sp_executesql N'ALTER TABLE dbo.Invoices ADD PublicLinkToken NVARCHAR(64) NULL;';
    PRINT 'Added Invoices.PublicLinkToken';
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'UX_Invoices_PublicLinkToken' AND object_id = OBJECT_ID(N'dbo.Invoices'))
BEGIN
    EXEC sp_executesql N'CREATE UNIQUE INDEX UX_Invoices_PublicLinkToken
                         ON dbo.Invoices(PublicLinkToken) WHERE PublicLinkToken IS NOT NULL;';
    PRINT 'Created UX_Invoices_PublicLinkToken';
END

-- ── InvoiceSentCopies ────────────────────────────────────────────────
IF OBJECT_ID(N'dbo.InvoiceSentCopies', N'U') IS NULL
BEGIN
    EXEC sp_executesql N'
    CREATE TABLE dbo.InvoiceSentCopies
    (
        InvoiceId     UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_InvoiceSentCopies PRIMARY KEY,
        TenantId      UNIQUEIDENTIFIER NOT NULL,
        FileName      NVARCHAR(255)    NOT NULL,
        SizeBytes     BIGINT           NOT NULL,
        PdfContent    VARBINARY(MAX)   NOT NULL,
        CreatedAtUtc  DATETIME2        NOT NULL CONSTRAINT DF_InvoiceSentCopies_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CreatedBy     NVARCHAR(200)    NULL,
        CONSTRAINT FK_InvoiceSentCopies_Invoice FOREIGN KEY (InvoiceId)
            REFERENCES dbo.Invoices(Id) ON DELETE CASCADE
    );';
    PRINT 'Created dbo.InvoiceSentCopies';
END
ELSE
    PRINT 'dbo.InvoiceSentCopies already exists — skipped';

-- ── Grid 1: everything present ───────────────────────────────────────
EXEC sp_executesql N'
SELECT N''OutboundMessages.CcAddresses'' AS [Item],
       CASE WHEN COL_LENGTH(N''dbo.OutboundMessages'', N''CcAddresses'') IS NOT NULL THEN N''OK'' ELSE N''MISSING'' END AS [Result]
UNION ALL SELECT N''OutboundMessages.SentByUserId'',
       CASE WHEN COL_LENGTH(N''dbo.OutboundMessages'', N''SentByUserId'') IS NOT NULL THEN N''OK'' ELSE N''MISSING'' END
UNION ALL SELECT N''OutboundMessages.DeliveryStatus'',
       CASE WHEN COL_LENGTH(N''dbo.OutboundMessages'', N''DeliveryStatus'') IS NOT NULL THEN N''OK'' ELSE N''MISSING'' END
UNION ALL SELECT N''OutboundMessages.DeliveryStatusAtUtc'',
       CASE WHEN COL_LENGTH(N''dbo.OutboundMessages'', N''DeliveryStatusAtUtc'') IS NOT NULL THEN N''OK'' ELSE N''MISSING'' END
UNION ALL SELECT N''OutboundMessages.DeliveryDetail'',
       CASE WHEN COL_LENGTH(N''dbo.OutboundMessages'', N''DeliveryDetail'') IS NOT NULL THEN N''OK'' ELSE N''MISSING'' END
UNION ALL SELECT N''IX_OutboundMessages_Provider'',
       CASE WHEN EXISTS (SELECT 1 FROM sys.indexes WHERE name = N''IX_OutboundMessages_Provider'') THEN N''OK'' ELSE N''MISSING'' END
UNION ALL SELECT N''dbo.OutboundMessageAttachments'',
       CASE WHEN OBJECT_ID(N''dbo.OutboundMessageAttachments'', N''U'') IS NOT NULL THEN N''OK'' ELSE N''MISSING'' END
UNION ALL SELECT N''Invoices.PublicLinkToken'',
       CASE WHEN COL_LENGTH(N''dbo.Invoices'', N''PublicLinkToken'') IS NOT NULL THEN N''OK'' ELSE N''MISSING'' END
UNION ALL SELECT N''UX_Invoices_PublicLinkToken'',
       CASE WHEN EXISTS (SELECT 1 FROM sys.indexes WHERE name = N''UX_Invoices_PublicLinkToken'') THEN N''OK'' ELSE N''MISSING'' END
UNION ALL SELECT N''dbo.InvoiceSentCopies'',
       CASE WHEN OBJECT_ID(N''dbo.InvoiceSentCopies'', N''U'') IS NOT NULL THEN N''OK'' ELSE N''MISSING'' END;';

IF @Commit = 1
BEGIN
    COMMIT TRAN;
    PRINT '085 COMMITTED.';
END
ELSE
BEGIN
    ROLLBACK TRAN;
    PRINT '085 dry run — rolled back. Set @Commit = 1 to apply.';
END
