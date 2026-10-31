-- ============================================================
-- MadeeAutomation database schema
-- Run this against the manually-created 'MadeeAutomation' database
-- Matches: AutomationDbContext (ClientConfigs, WorkflowDefinitions, ExecutionLogs)
-- ============================================================

USE MadeeAutomation;
GO

-- ------------------------------------------------------------
-- ClientConfigs
-- ------------------------------------------------------------
CREATE TABLE dbo.ClientConfigs
(
    Id                          UNIQUEIDENTIFIER   NOT NULL DEFAULT NEWID(),
    ClientName                  NVARCHAR(200)       NOT NULL,
    ClientCode                  NVARCHAR(50)        NOT NULL,
    Country                     NVARCHAR(10)        NOT NULL DEFAULT '',
    TimeZoneId                  NVARCHAR(100)       NOT NULL DEFAULT 'Asia/Bangkok',
    Currency                    NVARCHAR(10)        NOT NULL DEFAULT 'THB',
    Vertical                    INT                 NOT NULL,
    Status                      INT                 NOT NULL DEFAULT 1, -- 1 = Trial
    IntegrationSettingsJson     NVARCHAR(MAX)       NOT NULL DEFAULT '{}',
    MerkaiIntegrationEnabled    BIT                 NOT NULL DEFAULT 0,
    PosFirstIntegrationEnabled  BIT                 NOT NULL DEFAULT 0,
    CreatedAtUtc                DATETIME2           NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedAtUtc                DATETIME2           NULL,

    CONSTRAINT PK_ClientConfigs PRIMARY KEY (Id),
    CONSTRAINT UQ_ClientConfigs_ClientCode UNIQUE (ClientCode)
);
GO

-- ------------------------------------------------------------
-- WorkflowDefinitions
-- ------------------------------------------------------------
CREATE TABLE dbo.WorkflowDefinitions
(
    Id                  UNIQUEIDENTIFIER   NOT NULL DEFAULT NEWID(),
    ClientConfigId      UNIQUEIDENTIFIER   NOT NULL,
    Name                NVARCHAR(200)      NOT NULL,
    HandlerKey          NVARCHAR(100)      NOT NULL,
    Vertical            INT                NOT NULL,
    TriggerType         INT                NOT NULL,
    TriggerConfigJson   NVARCHAR(MAX)      NOT NULL DEFAULT '{}',
    IsEnabled           BIT                NOT NULL DEFAULT 1,
    CreatedAtUtc        DATETIME2          NOT NULL DEFAULT SYSUTCDATETIME(),
    UpdatedAtUtc        DATETIME2          NULL,

    CONSTRAINT PK_WorkflowDefinitions PRIMARY KEY (Id),
    CONSTRAINT FK_WorkflowDefinitions_ClientConfigs
        FOREIGN KEY (ClientConfigId) REFERENCES dbo.ClientConfigs (Id)
        ON DELETE CASCADE
);
GO

CREATE INDEX IX_WorkflowDefinitions_ClientConfigId
    ON dbo.WorkflowDefinitions (ClientConfigId);
GO

-- ------------------------------------------------------------
-- ExecutionLogs
-- ------------------------------------------------------------
CREATE TABLE dbo.ExecutionLogs
(
    Id                      UNIQUEIDENTIFIER   NOT NULL DEFAULT NEWID(),
    WorkflowDefinitionId    UNIQUEIDENTIFIER   NOT NULL,
    ClientConfigId          UNIQUEIDENTIFIER   NOT NULL,
    Status                  INT                NOT NULL DEFAULT 1, -- 1 = Pending
    TriggerPayloadJson      NVARCHAR(MAX)      NULL,
    ResultSummary           NVARCHAR(MAX)      NULL,
    ErrorMessage            NVARCHAR(MAX)      NULL,
    RetryCount              INT                NOT NULL DEFAULT 0,
    StartedAtUtc            DATETIME2          NOT NULL DEFAULT SYSUTCDATETIME(),
    CompletedAtUtc          DATETIME2          NULL,
    DurationMs              BIGINT             NULL,

    CONSTRAINT PK_ExecutionLogs PRIMARY KEY (Id),
    CONSTRAINT FK_ExecutionLogs_WorkflowDefinitions
        FOREIGN KEY (WorkflowDefinitionId) REFERENCES dbo.WorkflowDefinitions (Id)
        ON DELETE CASCADE
);
GO

-- Supports the Admin dashboard's "recent executions per client" query
CREATE INDEX IX_ExecutionLogs_ClientConfigId_StartedAtUtc
    ON dbo.ExecutionLogs (ClientConfigId, StartedAtUtc);
GO

CREATE INDEX IX_ExecutionLogs_WorkflowDefinitionId
    ON dbo.ExecutionLogs (WorkflowDefinitionId);
GO

-- ============================================================
-- Optional: seed your first client (MadeeBakers) + first workflow
-- Uncomment and adjust as needed.
-- ============================================================
/*
DECLARE @ClientId UNIQUEIDENTIFIER = NEWID();

INSERT INTO dbo.ClientConfigs
    (Id, ClientName, ClientCode, Country, TimeZoneId, Currency, Vertical, Status, PosFirstIntegrationEnabled)
VALUES
    (@ClientId, 'MadeeBakers', 'madeebakers', 'TH', 'Asia/Bangkok', 'THB', 1, 2, 1);
    -- Vertical 1 = FnBRetail, Status 2 = Active

INSERT INTO dbo.WorkflowDefinitions
    (Id, ClientConfigId, Name, HandlerKey, Vertical, TriggerType, TriggerConfigJson, IsEnabled)
VALUES
    (NEWID(), @ClientId, 'WhatsApp Order Intake', 'whatsapp-order-parser', 1, 3, '{}', 1);
    -- TriggerType 3 = MessageReceived
*/
