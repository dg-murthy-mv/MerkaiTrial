/* =====================================================================
   083_InboundEmail.sql — receiving email (replies, BCC, forwards)

   Adds:
     dbo.InboundEmails
         One row per email Resend received for a workspace. Written by the
         webhook the moment Resend calls it (sender, recipients, subject),
         then completed by the background worker (body, attachments, which
         lead / deal / contact it belongs to).

     dbo.InboundEmailAttachments
         Files that came with an email, stored the same way as every other
         attachment (IFileStorageService).

     dbo.EmailReplyTokens
         The random code in a tracked reply address,
         reply+<token>@<your receiving domain>. Points at the quote (or
         other record) the outgoing email was about. Kept for good: a
         customer may reply to a quote email months later, long after the
         outbox row itself has been cleaned up.

     dbo.Tenants.InboundAlias   NVARCHAR(64) NULL
         The workspace's own receiving address, the part before the @.
         Random, so it cannot be guessed from the workspace key. Filled in
         the first time someone opens Settings → Inbound Email.

   Nothing existing is changed or back-filled.

   HOW TO RUN
     1. Run as-is. @Commit = 0 — everything is ROLLED BACK. Grid 1: every
        row should say OK.
     2. Change @Commit to 1 and run again.

   SAFE TO RE-RUN. Every CREATE / ALTER is guarded.
   ===================================================================== */

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit BIT = 0;      -- flip to 1 to commit

BEGIN TRAN;

-- ── Tenants.InboundAlias ─────────────────────────────────────────────
IF COL_LENGTH(N'dbo.Tenants', N'InboundAlias') IS NULL
BEGIN
    EXEC sp_executesql N'ALTER TABLE dbo.Tenants ADD InboundAlias NVARCHAR(64) NULL;';
    PRINT 'Added Tenants.InboundAlias';
END
ELSE
    PRINT 'Tenants.InboundAlias already exists — skipped';

IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'UX_Tenants_InboundAlias' AND object_id = OBJECT_ID(N'dbo.Tenants'))
BEGIN
    EXEC sp_executesql N'CREATE UNIQUE INDEX UX_Tenants_InboundAlias
                         ON dbo.Tenants(InboundAlias) WHERE InboundAlias IS NOT NULL;';
    PRINT 'Created UX_Tenants_InboundAlias';
END

-- ── InboundEmails ────────────────────────────────────────────────────
IF OBJECT_ID(N'dbo.InboundEmails', N'U') IS NULL
BEGIN
    EXEC sp_executesql N'
    CREATE TABLE dbo.InboundEmails
    (
        Id                  UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_InboundEmails PRIMARY KEY,
        TenantId            UNIQUEIDENTIFIER NOT NULL,

        ProviderEmailId     NVARCHAR(100)    NOT NULL,   -- Resend email_id; the dedupe key
        MessageId           NVARCHAR(500)    NULL,       -- RFC Message-ID
        InReplyTo           NVARCHAR(500)    NULL,

        Direction           INT              NOT NULL CONSTRAINT DF_InboundEmails_Direction   DEFAULT (0),
        Status              INT              NOT NULL CONSTRAINT DF_InboundEmails_Status      DEFAULT (0),
        MatchMethod         INT              NOT NULL CONSTRAINT DF_InboundEmails_MatchMethod DEFAULT (0),

        FromAddress         NVARCHAR(320)    NOT NULL,
        FromName            NVARCHAR(200)    NULL,
        ToAddresses         NVARCHAR(2000)   NULL,
        CcAddresses         NVARCHAR(2000)   NULL,
        CounterpartAddress  NVARCHAR(320)    NULL,

        Subject             NVARCHAR(500)    NOT NULL CONSTRAINT DF_InboundEmails_Subject DEFAULT (N''''),
        Snippet             NVARCHAR(300)    NULL,
        BodyText            NVARCHAR(MAX)    NULL,
        BodyHtml            NVARCHAR(MAX)    NULL,

        IsAutoReply         BIT              NOT NULL CONSTRAINT DF_InboundEmails_IsAutoReply DEFAULT (0),
        SenderVerified      BIT              NULL,
        AuthSummary         NVARCHAR(100)    NULL,

        LeadId              UNIQUEIDENTIFIER NULL,
        DealId              UNIQUEIDENTIFIER NULL,
        ContactId           UNIQUEIDENTIFIER NULL,
        CompanyId           UNIQUEIDENTIFIER NULL,
        QuoteId             UNIQUEIDENTIFIER NULL,
        ForwardedByUserId   UNIQUEIDENTIFIER NULL,
        MatchNote           NVARCHAR(300)    NULL,

        AttachmentCount     INT              NOT NULL CONSTRAINT DF_InboundEmails_AttachmentCount DEFAULT (0),
        AttachmentNote      NVARCHAR(1000)   NULL,
        MetadataJson        NVARCHAR(MAX)    NULL,

        ProcessingState     INT              NOT NULL CONSTRAINT DF_InboundEmails_ProcessingState DEFAULT (0),
        ProcessingAttempts  INT              NOT NULL CONSTRAINT DF_InboundEmails_ProcessingAttempts DEFAULT (0),
        NextAttemptAtUtc    DATETIME2        NULL,
        LockedUntilUtc      DATETIME2        NULL,
        ProcessingError     NVARCHAR(1000)   NULL,

        ReceivedAtUtc       DATETIME2        NOT NULL,
        CreatedAtUtc        DATETIME2        NOT NULL CONSTRAINT DF_InboundEmails_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        ProcessedAtUtc      DATETIME2        NULL,
        LinkedBy            NVARCHAR(200)    NULL,
        LinkedAtUtc         DATETIME2        NULL,

        IsDeleted           BIT              NOT NULL CONSTRAINT DF_InboundEmails_IsDeleted DEFAULT (0),
        DeletedAtUtc        DATETIME2        NULL,
        DeletedBy           NVARCHAR(200)    NULL,

        CONSTRAINT FK_InboundEmails_Tenants FOREIGN KEY (TenantId) REFERENCES dbo.Tenants(Id)
    );';
    PRINT 'Created dbo.InboundEmails';
END
ELSE
    PRINT 'dbo.InboundEmails already exists — skipped';

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_InboundEmails_ProviderEmailId')
    EXEC sp_executesql N'CREATE UNIQUE INDEX UX_InboundEmails_ProviderEmailId ON dbo.InboundEmails(ProviderEmailId);';

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_InboundEmails_Tenant_Status')
    EXEC sp_executesql N'CREATE INDEX IX_InboundEmails_Tenant_Status
                         ON dbo.InboundEmails(TenantId, Status, ReceivedAtUtc DESC) WHERE IsDeleted = 0;';

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_InboundEmails_Tenant_Lead')
    EXEC sp_executesql N'CREATE INDEX IX_InboundEmails_Tenant_Lead
                         ON dbo.InboundEmails(TenantId, LeadId) WHERE LeadId IS NOT NULL AND IsDeleted = 0;';

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_InboundEmails_Tenant_Deal')
    EXEC sp_executesql N'CREATE INDEX IX_InboundEmails_Tenant_Deal
                         ON dbo.InboundEmails(TenantId, DealId) WHERE DealId IS NOT NULL AND IsDeleted = 0;';

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_InboundEmails_Tenant_Contact')
    EXEC sp_executesql N'CREATE INDEX IX_InboundEmails_Tenant_Contact
                         ON dbo.InboundEmails(TenantId, ContactId) WHERE ContactId IS NOT NULL AND IsDeleted = 0;';

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_InboundEmails_Tenant_MessageId')
    EXEC sp_executesql N'CREATE INDEX IX_InboundEmails_Tenant_MessageId
                         ON dbo.InboundEmails(TenantId, MessageId) WHERE MessageId IS NOT NULL;';

-- The worker's claim query: Pending (0) or Retry (3), due now.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_InboundEmails_Processing')
    EXEC sp_executesql N'CREATE INDEX IX_InboundEmails_Processing
                         ON dbo.InboundEmails(ProcessingState, NextAttemptAtUtc)
                         WHERE ProcessingState IN (0, 1, 3);';

-- ── InboundEmailAttachments ──────────────────────────────────────────
IF OBJECT_ID(N'dbo.InboundEmailAttachments', N'U') IS NULL
BEGIN
    EXEC sp_executesql N'
    CREATE TABLE dbo.InboundEmailAttachments
    (
        Id              UNIQUEIDENTIFIER NOT NULL CONSTRAINT PK_InboundEmailAttachments PRIMARY KEY,
        TenantId        UNIQUEIDENTIFIER NOT NULL,
        InboundEmailId  UNIQUEIDENTIFIER NOT NULL,
        FileName        NVARCHAR(255)    NOT NULL,
        FileUrl         NVARCHAR(1000)   NOT NULL,
        FileSize        BIGINT           NOT NULL,
        MimeType        NVARCHAR(200)    NULL,
        CreatedAtUtc    DATETIME2        NOT NULL CONSTRAINT DF_InboundEmailAttachments_CreatedAtUtc DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT FK_InboundEmailAttachments_Email FOREIGN KEY (InboundEmailId)
            REFERENCES dbo.InboundEmails(Id) ON DELETE CASCADE
    );';
    EXEC sp_executesql N'CREATE INDEX IX_InboundEmailAttachments_Email ON dbo.InboundEmailAttachments(InboundEmailId);';
    PRINT 'Created dbo.InboundEmailAttachments';
END
ELSE
    PRINT 'dbo.InboundEmailAttachments already exists — skipped';

-- ── EmailReplyTokens ─────────────────────────────────────────────────
IF OBJECT_ID(N'dbo.EmailReplyTokens', N'U') IS NULL
BEGIN
    EXEC sp_executesql N'
    CREATE TABLE dbo.EmailReplyTokens
    (
        Token              NVARCHAR(40)     NOT NULL CONSTRAINT PK_EmailReplyTokens PRIMARY KEY,
        TenantId           UNIQUEIDENTIFIER NOT NULL,
        EntityType         NVARCHAR(50)     NOT NULL,
        EntityId           UNIQUEIDENTIFIER NOT NULL,
        OutboundMessageId  UNIQUEIDENTIFIER NULL,
        CreatedAtUtc       DATETIME2        NOT NULL CONSTRAINT DF_EmailReplyTokens_CreatedAtUtc DEFAULT (SYSUTCDATETIME())
    );';
    EXEC sp_executesql N'CREATE INDEX IX_EmailReplyTokens_Entity ON dbo.EmailReplyTokens(TenantId, EntityType, EntityId);';
    PRINT 'Created dbo.EmailReplyTokens';
END
ELSE
    PRINT 'dbo.EmailReplyTokens already exists — skipped';

-- ── Grid 1: everything present ───────────────────────────────────────
EXEC sp_executesql N'
SELECT N''Tenants.InboundAlias'' AS [Item],
       CASE WHEN COL_LENGTH(N''dbo.Tenants'', N''InboundAlias'') IS NOT NULL THEN N''OK'' ELSE N''MISSING'' END AS [Result]
UNION ALL SELECT N''dbo.InboundEmails'',
       CASE WHEN OBJECT_ID(N''dbo.InboundEmails'', N''U'') IS NOT NULL THEN N''OK'' ELSE N''MISSING'' END
UNION ALL SELECT N''dbo.InboundEmailAttachments'',
       CASE WHEN OBJECT_ID(N''dbo.InboundEmailAttachments'', N''U'') IS NOT NULL THEN N''OK'' ELSE N''MISSING'' END
UNION ALL SELECT N''dbo.EmailReplyTokens'',
       CASE WHEN OBJECT_ID(N''dbo.EmailReplyTokens'', N''U'') IS NOT NULL THEN N''OK'' ELSE N''MISSING'' END
UNION ALL SELECT N''UX_InboundEmails_ProviderEmailId'',
       CASE WHEN EXISTS (SELECT 1 FROM sys.indexes WHERE name = N''UX_InboundEmails_ProviderEmailId'') THEN N''OK'' ELSE N''MISSING'' END
UNION ALL SELECT N''UX_Tenants_InboundAlias'',
       CASE WHEN EXISTS (SELECT 1 FROM sys.indexes WHERE name = N''UX_Tenants_InboundAlias'') THEN N''OK'' ELSE N''MISSING'' END;';

IF @Commit = 1
BEGIN
    COMMIT TRAN;
    PRINT '083 COMMITTED.';
END
ELSE
BEGIN
    ROLLBACK TRAN;
    PRINT '083 dry run — rolled back. Set @Commit = 1 to apply.';
END
