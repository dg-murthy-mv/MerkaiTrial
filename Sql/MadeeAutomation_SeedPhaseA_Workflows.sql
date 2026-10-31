-- ============================================================
-- Phase A test workflows: lead-scoring, invoice-extraction, faq-responder
-- Attached to clients whose vertical already matches, from the earlier
-- country seed script. Safe to re-run - each INSERT only fires if the
-- matching ClientCode exists (0 rows affected if it doesn't).
-- ============================================================

USE MadeeAutomation;
GO

-- CRM/Sales: lead-scoring -> Siam Trade Partners (TH)
INSERT INTO dbo.WorkflowDefinitions
    (Id, ClientConfigId, Name, HandlerKey, Vertical, TriggerType, TriggerConfigJson, IsEnabled)
SELECT
    NEWID(), Id, 'Web Lead Scoring', 'lead-scoring', 2, 1, '{}', 1
    -- Vertical 2 = CrmSales, TriggerType 1 = Webhook
FROM dbo.ClientConfigs
WHERE ClientCode = 'siamtrade';

-- Finance/Chit: invoice-extraction -> Cebu Fintech Solutions (PH)
INSERT INTO dbo.WorkflowDefinitions
    (Id, ClientConfigId, Name, HandlerKey, Vertical, TriggerType, TriggerConfigJson, IsEnabled)
SELECT
    NEWID(), Id, 'Invoice/Receipt Extraction', 'invoice-extraction', 3, 4, '{}', 1
    -- Vertical 3 = FinanceChit, TriggerType 4 = FileUploaded
FROM dbo.ClientConfigs
WHERE ClientCode = 'cebufintech';

-- Customer Support: faq-responder -> Emirates Business Support (AE)
INSERT INTO dbo.WorkflowDefinitions
    (Id, ClientConfigId, Name, HandlerKey, Vertical, TriggerType, TriggerConfigJson, IsEnabled)
SELECT
    NEWID(), Id, 'WhatsApp FAQ Responder', 'faq-responder', 4, 3, '{}', 1
    -- Vertical 4 = CustomerSupport, TriggerType 3 = MessageReceived
FROM dbo.ClientConfigs
WHERE ClientCode = 'emiratessupport';
GO

-- Grab the ClientCode + WorkflowDefinitionId + endpoint you need for each Swagger test
SELECT
    c.ClientCode,
    c.ClientName,
    w.Id AS WorkflowDefinitionId,
    w.Name,
    w.HandlerKey,
    CASE w.HandlerKey
        WHEN 'lead-scoring' THEN CONCAT('/api/webhooks/generic/', c.ClientCode, '/', w.Id)
        WHEN 'invoice-extraction' THEN CONCAT('/api/webhooks/generic/', c.ClientCode, '/', w.Id)
        WHEN 'faq-responder' THEN CONCAT('/api/webhooks/generic/', c.ClientCode, '/', w.Id)
    END AS TestEndpoint
FROM dbo.WorkflowDefinitions w
JOIN dbo.ClientConfigs c ON c.Id = w.ClientConfigId
WHERE w.HandlerKey IN ('lead-scoring', 'invoice-extraction', 'faq-responder')
ORDER BY c.ClientCode;
GO
