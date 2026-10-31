-- ══════════════════════════════════════════════════════════════════════
-- India (Nexora Tech Solutions) + UAE (Al Fardan Trading) demo pipeline
--
-- Converts 4 existing leads per tenant into Companies → Contacts → Deals,
-- with Quotes/Invoices/Payments for the ones that get that far — mirrors
-- the exact pattern already present in Thailand/Philippines data, using
-- REAL existing Lead and Product rows rather than inventing new ones.
--
-- Mix per tenant: 1 ClosedWon (quote accepted → invoice → paid in full),
-- 1 Negotiation (active, no quote yet), 1 Proposal (quote sent, not yet
-- accepted), 1 ClosedLost (quote rejected, with a LostReason).
--
-- Wrapped in a transaction with a verification SELECT before COMMIT —
-- same pattern as the earlier tenant rebrand script.
-- ══════════════════════════════════════════════════════════════════════

BEGIN TRANSACTION;

DECLARE @Now DATETIME2(7) = SYSUTCDATETIME();

-- ══════════════════════════════════════════════════════════════════════
-- INDIA — Nexora Tech Solutions (BB100001-AAAA-0000-0000-000000000001)
-- Sales team: Priya Nair (manager, ...010), Arjun Mehta (rep, ...011)
-- ══════════════════════════════════════════════════════════════════════

-- ── Deal 1: Arjun Mehta / HD Bank Corp → ClosedWon ──────────────────────
-- Owner: Priya Nair (NOT Arjun Mehta — avoids owner/contact name collision)
INSERT INTO [dbo].[Companies] (Id, Name, Country, TaxId, CreatedAtUtc, CreatedBy, UpdatedAtUtc, UpdatedBy, IsDeleted, Vertical, TenantId)
VALUES ('F375CB74-6BDE-456C-9CCE-045C4F2E1042', N'HD Bank Corp', 'IN', NULL, @Now, 'Priya Nair', NULL, NULL, 0, 'Finance', 'BB100001-AAAA-0000-0000-000000000001');

INSERT INTO [dbo].[Contacts] (Id, CompanyId, FirstName, LastName, Email, Phone, LineUserId, CreatedAtUtc, CreatedBy, UpdatedAtUtc, UpdatedBy, IsDeleted, TenantId, JobTitle, Mobile, Address, City, Country, PostalCode, Notes, IsPrimary)
VALUES ('9AB19FF1-A0FB-47EE-BC37-F93A6EA1B1ED', 'F375CB74-6BDE-456C-9CCE-045C4F2E1042', N'Arjun', N'Mehta', 'arjun.mehta@hdbankcorp.in', '+91-87654-22002', NULL, @Now, 'Priya Nair', @Now, 'Priya Nair', 0, 'BB100001-AAAA-0000-0000-000000000001', NULL, NULL, NULL, NULL, 'IN', NULL, NULL, 1);

INSERT INTO [dbo].[Deals] (Id, TenantId, ContactId, LeadId, Title, Description, Stage, Priority, Probability, ExpectedValue, ActualValue, ExpectedCloseDateUtc, ActualCloseDateUtc, OwnerUserId, Tags, CustomFields, CreatedAtUtc, CreatedBy, UpdatedAtUtc, UpdatedBy, IsDeleted, DeletedAtUtc, DeletedBy, CompanyId, LostReason, SourceId, Currency, VerticalId, Source)
VALUES ('486B14AC-A6F1-4EF1-A833-25C59BF55078', 'BB100001-AAAA-0000-0000-000000000001', '9AB19FF1-A0FB-47EE-BC37-F93A6EA1B1ED', 'FEED0001-0002-0000-0000-000000000002',
        N'Deal - Arjun Mehta', N'Full CRM rollout across retail banking division', 'ClosedWon', NULL, 100, 88498.82, 88498.82,
        DATEADD(DAY, -5, @Now), DATEADD(DAY, -3, @Now), 'DD100001-DD00-0000-0000-000000000010', NULL, NULL,
        @Now, 'Priya Nair', @Now, 'Priya Nair', 0, NULL, NULL, 'F375CB74-6BDE-456C-9CCE-045C4F2E1042', NULL, NULL, 'INR', 'CB68101F-EF01-466E-B671-27333FA0924B', NULL);

INSERT INTO [dbo].[Quotes] (Id, DealId, Number, IssueDateUtc, ExpiresAtUtc, Currency, Status, Subtotal, DiscountTotal, TaxTotal, GrandTotal, PaymentLinkUrl, PdfUrl, CreatedAtUtc, CreatedBy, UpdatedAtUtc, UpdatedBy, IsDeleted, TenantId, PublicLinkToken)
VALUES ('255B7E76-8633-460A-92B3-64E3D8342A7A', '486B14AC-A6F1-4EF1-A833-25C59BF55078', 'QUO-0001', DATEADD(DAY, -8, @Now), DATEADD(DAY, 22, @Now), 'INR', 3,
        74999.00, 0.00, 13499.82, 88498.82, NULL, NULL, DATEADD(DAY, -8, @Now), 'Priya Nair', DATEADD(DAY, -5, @Now), 'Priya Nair', 0, 'BB100001-AAAA-0000-0000-000000000001', NULL);

INSERT INTO [dbo].[QuoteItems] (Id, QuoteId, ProductId, Description, UnitPrice, Quantity, LineDiscount, TaxRate, CreatedAtUtc, CreatedBy, UpdatedAtUtc, UpdatedBy, IsDeleted, TenantId, Name)
VALUES
    ('8238A51F-D0A1-4F8A-9C1C-875F5334B88A', '255B7E76-8633-460A-92B3-64E3D8342A7A', '72CBBA83-9B2F-4EBE-93EE-1EEB00EE4982', N'Unlimited users, white-label, API access, dedicated support, SLA 99.9%.', 49999.00, 1, 0.00, 0.1800, DATEADD(DAY, -8, @Now), 'Priya Nair', NULL, NULL, 0, 'BB100001-AAAA-0000-0000-000000000001', N'CRM Enterprise Plan'),
    ('38EBFF75-BB35-419E-BBF7-80DACA6DB2D8', '255B7E76-8633-460A-92B3-64E3D8342A7A', '24414FC6-D45D-4672-B813-3E3945F9DB9A', N'Standard onboarding: data migration, 2 training sessions, go-live support.', 25000.00, 1, 0.00, 0.1800, DATEADD(DAY, -8, @Now), 'Priya Nair', NULL, NULL, 0, 'BB100001-AAAA-0000-0000-000000000001', N'Implementation & Onboarding');

INSERT INTO [dbo].[Invoices] (Id, QuoteId, Number, IssueDateUtc, DueDateUtc, Currency, Status, Total, Balance, PromptPayQrImageUrl, GatewayRef, CreatedAtUtc, CreatedBy, UpdatedAtUtc, UpdatedBy, IsDeleted, TenantId, Amount, Provider, ProviderRef, CheckoutUrl, DealId, Subtotal, DiscountTotal, TaxTotal, PdfUrl, Notes)
VALUES ('47980595-661C-4BF0-9403-8EED74D5D8F1', '255B7E76-8633-460A-92B3-64E3D8342A7A', 'INV-0001', DATEADD(DAY, -5, @Now), DATEADD(DAY, 25, @Now), 'INR', 4,
        88498.82, 0.00, NULL, NULL, DATEADD(DAY, -5, @Now), 'Priya Nair', DATEADD(DAY, -3, @Now), 'Priya Nair', 0, 'BB100001-AAAA-0000-0000-000000000001',
        88498.82, NULL, NULL, NULL, '486B14AC-A6F1-4EF1-A833-25C59BF55078', 74999.00, 0.00, 13499.82, NULL, NULL);

INSERT INTO [dbo].[InvoiceLines] (Id, InvoiceId, Description, Amount, CreatedAtUtc, CreatedBy, UpdatedAtUtc, UpdatedBy, IsDeleted, TenantId, ProductId, Name, UnitPrice, Quantity, LineDiscount, TaxRate)
VALUES
    ('54DAE56E-26BC-4D4D-841C-015773B305EE', '47980595-661C-4BF0-9403-8EED74D5D8F1', N'Unlimited users, white-label, API access, dedicated support, SLA 99.9%.', 58998.82, DATEADD(DAY, -5, @Now), 'Priya Nair', NULL, NULL, 0, 'BB100001-AAAA-0000-0000-000000000001', '72CBBA83-9B2F-4EBE-93EE-1EEB00EE4982', N'CRM Enterprise Plan', 49999.00, 1, 0.00, 0.1800),
    ('7FD7FBC3-BACF-49F9-87D6-8B4A43B13559', '47980595-661C-4BF0-9403-8EED74D5D8F1', N'Standard onboarding: data migration, 2 training sessions, go-live support.', 29500.00, DATEADD(DAY, -5, @Now), 'Priya Nair', NULL, NULL, 0, 'BB100001-AAAA-0000-0000-000000000001', '24414FC6-D45D-4672-B813-3E3945F9DB9A', N'Implementation & Onboarding', 25000.00, 1, 0.00, 0.1800);

INSERT INTO [dbo].[Payments] (Id, InvoiceId, Amount, Currency, Method, Status, ProviderTxnId, PaidAtUtc, CreatedAtUtc, CreatedBy, UpdatedAtUtc, UpdatedBy, IsDeleted, TenantId, Notes)
VALUES ('115B1FBD-D593-4A44-8B75-37ADDFC57CC7', '47980595-661C-4BF0-9403-8EED74D5D8F1', 88498.82, 'INR', 'BankTransfer', 'Captured', NULL, DATEADD(DAY, -3, @Now), DATEADD(DAY, -3, @Now), 'Priya Nair', NULL, NULL, 0, 'BB100001-AAAA-0000-0000-000000000001', N'full payment');

UPDATE [dbo].[Leads]
SET Status = 4, IsConverted = 1, ConvertedToContactId = '9AB19FF1-A0FB-47EE-BC37-F93A6EA1B1ED',
    ConvertedToCompanyId = 'F375CB74-6BDE-456C-9CCE-045C4F2E1042', ConvertedAtUtc = DATEADD(DAY, -8, @Now),
    ConvertedBy = 'Priya Nair', DealId = '486B14AC-A6F1-4EF1-A833-25C59BF55078',
    UpdatedAtUtc = @Now, UpdatedBy = 'DemoSeed'
WHERE Id = 'FEED0001-0002-0000-0000-000000000002';


-- ── Deal 2: Suresh Pillai / Kerala Logistics Co. → Negotiation ──────────
-- Active pipeline deal, no quote sent yet.
INSERT INTO [dbo].[Companies] (Id, Name, Country, TaxId, CreatedAtUtc, CreatedBy, UpdatedAtUtc, UpdatedBy, IsDeleted, Vertical, TenantId)
VALUES ('B1950FEC-C168-4AA7-A91E-892E837451B1', N'Kerala Logistics Co.', 'IN', NULL, @Now, 'Arjun Mehta', NULL, NULL, 0, 'Logistics', 'BB100001-AAAA-0000-0000-000000000001');

INSERT INTO [dbo].[Contacts] (Id, CompanyId, FirstName, LastName, Email, Phone, LineUserId, CreatedAtUtc, CreatedBy, UpdatedAtUtc, UpdatedBy, IsDeleted, TenantId, JobTitle, Mobile, Address, City, Country, PostalCode, Notes, IsPrimary)
VALUES ('2D27C560-C2D3-4E28-A500-25D8C5F08D06', 'B1950FEC-C168-4AA7-A91E-892E837451B1', N'Suresh', N'Pillai', 'suresh@keralalogs.in', '+91 91567 89012', NULL, @Now, 'Arjun Mehta', @Now, 'Arjun Mehta', 0, 'BB100001-AAAA-0000-0000-000000000001', NULL, NULL, NULL, NULL, 'IN', NULL, NULL, 1);

INSERT INTO [dbo].[Deals] (Id, TenantId, ContactId, LeadId, Title, Description, Stage, Priority, Probability, ExpectedValue, ActualValue, ExpectedCloseDateUtc, ActualCloseDateUtc, OwnerUserId, Tags, CustomFields, CreatedAtUtc, CreatedBy, UpdatedAtUtc, UpdatedBy, IsDeleted, DeletedAtUtc, DeletedBy, CompanyId, LostReason, SourceId, Currency, VerticalId, Source)
VALUES ('FA64ED9B-D9EE-41CC-8338-5168D0B08EB2', 'BB100001-AAAA-0000-0000-000000000001', '2D27C560-C2D3-4E28-A500-25D8C5F08D06', 'A4EFAA41-DDE2-4130-ADCF-4F03003173E4',
        N'Deal - Suresh Pillai', N'Fleet tracking + CRM integration, evaluating against 2 other vendors', 'Negotiation', NULL, 60, 140000.00, NULL,
        DATEADD(DAY, 12, @Now), NULL, 'DD100001-DD00-0000-0000-000000000011', NULL, NULL,
        @Now, 'Arjun Mehta', @Now, 'Arjun Mehta', 0, NULL, NULL, 'B1950FEC-C168-4AA7-A91E-892E837451B1', NULL, NULL, 'INR', 'EC90538D-4CCA-45E0-885C-054172F024F2', NULL);

UPDATE [dbo].[Leads]
SET Status = 4, IsConverted = 1, ConvertedToContactId = '2D27C560-C2D3-4E28-A500-25D8C5F08D06',
    ConvertedToCompanyId = 'B1950FEC-C168-4AA7-A91E-892E837451B1', ConvertedAtUtc = @Now,
    ConvertedBy = 'Arjun Mehta', DealId = 'FA64ED9B-D9EE-41CC-8338-5168D0B08EB2',
    UpdatedAtUtc = @Now, UpdatedBy = 'DemoSeed'
WHERE Id = 'A4EFAA41-DDE2-4130-ADCF-4F03003173E4';


-- ── Deal 3: Arun Kapoor / RetailHub Enterprises → Proposal ──────────────
-- Quote sent, awaiting response.
INSERT INTO [dbo].[Companies] (Id, Name, Country, TaxId, CreatedAtUtc, CreatedBy, UpdatedAtUtc, UpdatedBy, IsDeleted, Vertical, TenantId)
VALUES ('ADF82113-6378-45CB-B97F-2453379DD575', N'RetailHub Enterprises', 'IN', NULL, @Now, 'Priya Nair', NULL, NULL, 0, 'Retail', 'BB100001-AAAA-0000-0000-000000000001');

INSERT INTO [dbo].[Contacts] (Id, CompanyId, FirstName, LastName, Email, Phone, LineUserId, CreatedAtUtc, CreatedBy, UpdatedAtUtc, UpdatedBy, IsDeleted, TenantId, JobTitle, Mobile, Address, City, Country, PostalCode, Notes, IsPrimary)
VALUES ('A08B8688-D816-4B44-9D4B-BDB40D08C028', 'ADF82113-6378-45CB-B97F-2453379DD575', N'Arun', N'Kapoor', 'arun.kapoor@retailhub.in', '+91 91234 56789', NULL, @Now, 'Priya Nair', @Now, 'Priya Nair', 0, 'BB100001-AAAA-0000-0000-000000000001', NULL, NULL, N'15 Park Street, Mumbai, Maharashtra', NULL, 'IN', NULL, NULL, 1);

INSERT INTO [dbo].[Deals] (Id, TenantId, ContactId, LeadId, Title, Description, Stage, Priority, Probability, ExpectedValue, ActualValue, ExpectedCloseDateUtc, ActualCloseDateUtc, OwnerUserId, Tags, CustomFields, CreatedAtUtc, CreatedBy, UpdatedAtUtc, UpdatedBy, IsDeleted, DeletedAtUtc, DeletedBy, CompanyId, LostReason, SourceId, Currency, VerticalId, Source)
VALUES ('1F7519EE-373C-4DDE-AAD5-2EFC06255699', 'BB100001-AAAA-0000-0000-000000000001', 'A08B8688-D816-4B44-9D4B-BDB40D08C028', '9A41EE6A-BED3-4B5B-8B25-5B5B8033CA3B',
        N'Deal - Arun Kapoor', N'Multi-store rollout, 10 locations across Mumbai', 'Proposal', NULL, 40, 163418.20, NULL,
        DATEADD(DAY, 18, @Now), NULL, 'DD100001-DD00-0000-0000-000000000010', NULL, NULL,
        @Now, 'Priya Nair', @Now, 'Priya Nair', 0, NULL, NULL, 'ADF82113-6378-45CB-B97F-2453379DD575', NULL, NULL, 'INR', '0E5A91F8-5EBB-4546-9B5B-9BA88CCD9772', NULL);

INSERT INTO [dbo].[Quotes] (Id, DealId, Number, IssueDateUtc, ExpiresAtUtc, Currency, Status, Subtotal, DiscountTotal, TaxTotal, GrandTotal, PaymentLinkUrl, PdfUrl, CreatedAtUtc, CreatedBy, UpdatedAtUtc, UpdatedBy, IsDeleted, TenantId, PublicLinkToken)
VALUES ('A503E176-9F15-4B22-B9C2-BB2D929C5172', '1F7519EE-373C-4DDE-AAD5-2EFC06255699', 'QUO-0002', DATEADD(DAY, -2, @Now), DATEADD(DAY, 28, @Now), 'INR', 1,
        138490.00, 0.00, 24928.20, 163418.20, NULL, NULL, DATEADD(DAY, -2, @Now), 'Priya Nair', DATEADD(DAY, -2, @Now), 'Priya Nair', 0, 'BB100001-AAAA-0000-0000-000000000001', NULL);

INSERT INTO [dbo].[QuoteItems] (Id, QuoteId, ProductId, Description, UnitPrice, Quantity, LineDiscount, TaxRate, CreatedAtUtc, CreatedBy, UpdatedAtUtc, UpdatedBy, IsDeleted, TenantId, Name)
VALUES
    ('20657E5C-0948-4C6E-AD36-BD1FC1522BDB', 'A503E176-9F15-4B22-B9C2-BB2D929C5172', 'EECD5650-127E-48D9-8AAF-EF52445626FB', N'Full CRM suite — Leads, Deals, Contacts, Pipeline. Up to 10 users. Monthly billing.', 12999.00, 10, 0.00, 0.1800, DATEADD(DAY, -2, @Now), 'Priya Nair', NULL, NULL, 0, 'BB100001-AAAA-0000-0000-000000000001', N'CRM Professional Plan'),
    ('C82168E5-F234-4554-A56F-DAD824A76666', 'A503E176-9F15-4B22-B9C2-BB2D929C5172', '76141ECC-3F09-468F-BB57-787577160767', N'On-site or virtual training for up to 15 users. Half-day session, includes materials.', 8500.00, 1, 0.00, 0.1800, DATEADD(DAY, -2, @Now), 'Priya Nair', NULL, NULL, 0, 'BB100001-AAAA-0000-0000-000000000001', N'Group Training Session');

UPDATE [dbo].[Leads]
SET Status = 4, IsConverted = 1, ConvertedToContactId = 'A08B8688-D816-4B44-9D4B-BDB40D08C028',
    ConvertedToCompanyId = 'ADF82113-6378-45CB-B97F-2453379DD575', ConvertedAtUtc = @Now,
    ConvertedBy = 'Priya Nair', DealId = '1F7519EE-373C-4DDE-AAD5-2EFC06255699',
    UpdatedAtUtc = @Now, UpdatedBy = 'DemoSeed'
WHERE Id = '9A41EE6A-BED3-4B5B-8B25-5B5B8033CA3B';


-- ── Deal 4: Priya Mehta / TechSolutions India Pvt Ltd → ClosedLost ──────
INSERT INTO [dbo].[Companies] (Id, Name, Country, TaxId, CreatedAtUtc, CreatedBy, UpdatedAtUtc, UpdatedBy, IsDeleted, Vertical, TenantId)
VALUES ('9B5A0645-4942-42C9-82CA-C8003535E924', N'TechSolutions India Pvt Ltd', 'IN', NULL, @Now, 'Arjun Mehta', NULL, NULL, 0, 'Technology', 'BB100001-AAAA-0000-0000-000000000001');

INSERT INTO [dbo].[Contacts] (Id, CompanyId, FirstName, LastName, Email, Phone, LineUserId, CreatedAtUtc, CreatedBy, UpdatedAtUtc, UpdatedBy, IsDeleted, TenantId, JobTitle, Mobile, Address, City, Country, PostalCode, Notes, IsPrimary)
VALUES ('AE8991DC-47AF-4A1B-971E-556E4DD176BB', '9B5A0645-4942-42C9-82CA-C8003535E924', N'Priya', N'Mehta', 'priya.mehta@techsolutions.in', '+91 98765 43210', NULL, @Now, 'Arjun Mehta', @Now, 'Arjun Mehta', 0, 'BB100001-AAAA-0000-0000-000000000001', NULL, NULL, N'42 MG Road, Bengaluru, Karnataka', NULL, 'IN', NULL, NULL, 1);

INSERT INTO [dbo].[Deals] (Id, TenantId, ContactId, LeadId, Title, Description, Stage, Priority, Probability, ExpectedValue, ActualValue, ExpectedCloseDateUtc, ActualCloseDateUtc, OwnerUserId, Tags, CustomFields, CreatedAtUtc, CreatedBy, UpdatedAtUtc, UpdatedBy, IsDeleted, DeletedAtUtc, DeletedBy, CompanyId, LostReason, SourceId, Currency, VerticalId, Source)
VALUES ('6436C08C-5ACC-44FB-A048-084FAE263B98', 'BB100001-AAAA-0000-0000-000000000001', 'AE8991DC-47AF-4A1B-971E-556E4DD176BB', 'F7FFEB98-0459-405B-B274-993273B666BE',
        N'Deal - Priya Mehta', N'Evaluated our Professional plan against a lower-cost local competitor', 'ClosedLost', NULL, 0, 250000.00, NULL,
        DATEADD(DAY, -1, @Now), DATEADD(DAY, -1, @Now), 'DD100001-DD00-0000-0000-000000000011', NULL, NULL,
        @Now, 'Arjun Mehta', @Now, 'Arjun Mehta', 0, NULL, NULL, '9B5A0645-4942-42C9-82CA-C8003535E924',
        N'Went with a competitor offering a lower upfront cost.', NULL, 'INR', '5FF30010-D927-47B9-966C-DA2B686410E7', NULL);

INSERT INTO [dbo].[Quotes] (Id, DealId, Number, IssueDateUtc, ExpiresAtUtc, Currency, Status, Subtotal, DiscountTotal, TaxTotal, GrandTotal, PaymentLinkUrl, PdfUrl, CreatedAtUtc, CreatedBy, UpdatedAtUtc, UpdatedBy, IsDeleted, TenantId, PublicLinkToken)
VALUES ('7707199B-887F-4290-B47B-9DCCF87F7401', '6436C08C-5ACC-44FB-A048-084FAE263B98', 'QUO-0003', DATEADD(DAY, -10, @Now), DATEADD(DAY, -1, @Now), 'INR', 4,
        67999.00, 0.00, 12239.82, 80238.82, NULL, NULL, DATEADD(DAY, -10, @Now), 'Arjun Mehta', DATEADD(DAY, -1, @Now), 'Arjun Mehta', 0, 'BB100001-AAAA-0000-0000-000000000001', NULL);

INSERT INTO [dbo].[QuoteItems] (Id, QuoteId, ProductId, Description, UnitPrice, Quantity, LineDiscount, TaxRate, CreatedAtUtc, CreatedBy, UpdatedAtUtc, UpdatedBy, IsDeleted, TenantId, Name)
VALUES
    ('0592383B-1C2F-4A0D-9F1F-CA7C60DA3CFC', '7707199B-887F-4290-B47B-9DCCF87F7401', '72CBBA83-9B2F-4EBE-93EE-1EEB00EE4982', N'Unlimited users, white-label, API access, dedicated support, SLA 99.9%.', 49999.00, 1, 0.00, 0.1800, DATEADD(DAY, -10, @Now), 'Arjun Mehta', NULL, NULL, 0, 'BB100001-AAAA-0000-0000-000000000001', N'CRM Enterprise Plan'),
    ('BC36145D-474A-463B-B270-6222A4EE7F00', '7707199B-887F-4290-B47B-9DCCF87F7401', '20CDFC73-1D44-4F5C-A1C9-6C73B9E20A79', N'Priority email + phone support, 4hr SLA, quarterly health checks. 12 months.', 18000.00, 1, 0.00, 0.1800, DATEADD(DAY, -10, @Now), 'Arjun Mehta', NULL, NULL, 0, 'BB100001-AAAA-0000-0000-000000000001', N'Annual Support Contract');

UPDATE [dbo].[Leads]
SET Status = 4, IsConverted = 1, ConvertedToContactId = 'AE8991DC-47AF-4A1B-971E-556E4DD176BB',
    ConvertedToCompanyId = '9B5A0645-4942-42C9-82CA-C8003535E924', ConvertedAtUtc = @Now,
    ConvertedBy = 'Arjun Mehta', DealId = '6436C08C-5ACC-44FB-A048-084FAE263B98',
    UpdatedAtUtc = @Now, UpdatedBy = 'DemoSeed'
WHERE Id = 'F7FFEB98-0459-405B-B274-993273B666BE';


-- ══════════════════════════════════════════════════════════════════════
-- UAE — Al Fardan Trading LLC (BB100004-AAAA-0000-0000-000000000004)
-- Sales team: Fatima Al-Zaabi (manager, ...010), Omar Hassan (rep, ...011)
-- ══════════════════════════════════════════════════════════════════════

-- ── Deal 5: Mohammed Al Farsi / Alpha Engineering & Construction LLC → ClosedWon ──
INSERT INTO [dbo].[Companies] (Id, Name, Country, TaxId, CreatedAtUtc, CreatedBy, UpdatedAtUtc, UpdatedBy, IsDeleted, Vertical, TenantId)
VALUES ('1BEF179E-C9A7-4AA2-A15D-66CD184A6951', N'Alpha Engineering & Construction LLC', 'AE', NULL, @Now, 'Fatima Al-Zaabi', NULL, NULL, 0, 'Engineering Construction', 'BB100004-AAAA-0000-0000-000000000004');

INSERT INTO [dbo].[Contacts] (Id, CompanyId, FirstName, LastName, Email, Phone, LineUserId, CreatedAtUtc, CreatedBy, UpdatedAtUtc, UpdatedBy, IsDeleted, TenantId, JobTitle, Mobile, Address, City, Country, PostalCode, Notes, IsPrimary)
VALUES ('7B301291-783A-4FC0-9905-28F326687803', '1BEF179E-C9A7-4AA2-A15D-66CD184A6951', N'Mohammed', N'Al Farsi', 'm.alfarsi@alphaeng.ae', '+971-50-111-3001', NULL, @Now, 'Fatima Al-Zaabi', @Now, 'Fatima Al-Zaabi', 0, 'BB100004-AAAA-0000-0000-000000000004', NULL, NULL, N'Office 704, Maze Tower, Sheikh Zayed Rd, Dubai', NULL, 'AE', NULL, NULL, 1);

INSERT INTO [dbo].[Deals] (Id, TenantId, ContactId, LeadId, Title, Description, Stage, Priority, Probability, ExpectedValue, ActualValue, ExpectedCloseDateUtc, ActualCloseDateUtc, OwnerUserId, Tags, CustomFields, CreatedAtUtc, CreatedBy, UpdatedAtUtc, UpdatedBy, IsDeleted, DeletedAtUtc, DeletedBy, CompanyId, LostReason, SourceId, Currency, VerticalId, Source)
VALUES ('5AFB6ECA-9EEF-40EC-B671-D78F5BCCA016', 'BB100004-AAAA-0000-0000-000000000004', '7B301291-783A-4FC0-9905-28F326687803', 'FEED0004-0001-0000-0000-000000000001',
        N'Deal - Mohammed Al Farsi', N'Annual CRM + setup for site management team', 'ClosedWon', NULL, 100, 74642.40, 74642.40,
        DATEADD(DAY, -6, @Now), DATEADD(DAY, -2, @Now), 'DD100004-DD00-0000-0000-000000000010', NULL, NULL,
        @Now, 'Fatima Al-Zaabi', @Now, 'Fatima Al-Zaabi', 0, NULL, NULL, '1BEF179E-C9A7-4AA2-A15D-66CD184A6951', NULL, NULL, 'AED', '80AC7DF3-3AB4-4B95-81DB-9BED0C1C0820', NULL);

INSERT INTO [dbo].[Quotes] (Id, DealId, Number, IssueDateUtc, ExpiresAtUtc, Currency, Status, Subtotal, DiscountTotal, TaxTotal, GrandTotal, PaymentLinkUrl, PdfUrl, CreatedAtUtc, CreatedBy, UpdatedAtUtc, UpdatedBy, IsDeleted, TenantId, PublicLinkToken)
VALUES ('DBFDF96D-857B-4D62-A535-71D520F8B5E0', '5AFB6ECA-9EEF-40EC-B671-D78F5BCCA016', 'QUO-0001', DATEADD(DAY, -9, @Now), DATEADD(DAY, 21, @Now), 'AED', 3,
        71088.00, 0.00, 3554.40, 74642.40, NULL, NULL, DATEADD(DAY, -9, @Now), 'Fatima Al-Zaabi', DATEADD(DAY, -6, @Now), 'Fatima Al-Zaabi', 0, 'BB100004-AAAA-0000-0000-000000000004', NULL);

INSERT INTO [dbo].[QuoteItems] (Id, QuoteId, ProductId, Description, UnitPrice, Quantity, LineDiscount, TaxRate, CreatedAtUtc, CreatedBy, UpdatedAtUtc, UpdatedBy, IsDeleted, TenantId, Name)
VALUES
    ('9FA2B4C9-DE66-4253-AB27-9ECF37708DAC', 'DBFDF96D-857B-4D62-A535-71D520F8B5E0', '868A70B6-7DFE-413A-9D93-0EF82D137138', N'Unlimited users, white-label branding, API access, dedicated support manager, 99.9% SLA.', 4999.00, 12, 0.00, 0.0500, DATEADD(DAY, -9, @Now), 'Fatima Al-Zaabi', NULL, NULL, 0, 'BB100004-AAAA-0000-0000-000000000004', N'CRM Enterprise Plan'),
    ('7759AC2E-AE31-4058-830C-80833170B2B5', 'DBFDF96D-857B-4D62-A535-71D520F8B5E0', '1BF2244D-6872-42E6-BC0E-97CF4F15D7B6', N'Full setup, data migration, custom fields configuration, 3 training sessions, on-site go-live support.', 7500.00, 1, 0.00, 0.0500, DATEADD(DAY, -9, @Now), 'Fatima Al-Zaabi', NULL, NULL, 0, 'BB100004-AAAA-0000-0000-000000000004', N'Premium Implementation Package'),
    ('9D56F9B4-0EA7-4CC2-AB82-72EF6DA9BF93', 'DBFDF96D-857B-4D62-A535-71D520F8B5E0', '577CA65B-17D1-4863-9858-77DC16B115DD', N'Dedicated WhatsApp support line, 2hr SLA, quarterly business reviews, free minor upgrades.', 3600.00, 1, 0.00, 0.0500, DATEADD(DAY, -9, @Now), 'Fatima Al-Zaabi', NULL, NULL, 0, 'BB100004-AAAA-0000-0000-000000000004', N'Annual Support & Maintenance');

INSERT INTO [dbo].[Invoices] (Id, QuoteId, Number, IssueDateUtc, DueDateUtc, Currency, Status, Total, Balance, PromptPayQrImageUrl, GatewayRef, CreatedAtUtc, CreatedBy, UpdatedAtUtc, UpdatedBy, IsDeleted, TenantId, Amount, Provider, ProviderRef, CheckoutUrl, DealId, Subtotal, DiscountTotal, TaxTotal, PdfUrl, Notes)
VALUES ('01D0B264-982D-42FD-91F5-E34A32F5889F', 'DBFDF96D-857B-4D62-A535-71D520F8B5E0', 'INV-0001', DATEADD(DAY, -2, @Now), DATEADD(DAY, 28, @Now), 'AED', 4,
        74642.40, 0.00, NULL, NULL, DATEADD(DAY, -2, @Now), 'Fatima Al-Zaabi', DATEADD(DAY, -1, @Now), 'Fatima Al-Zaabi', 0, 'BB100004-AAAA-0000-0000-000000000004',
        74642.40, NULL, NULL, NULL, '5AFB6ECA-9EEF-40EC-B671-D78F5BCCA016', 71088.00, 0.00, 3554.40, NULL, NULL);

INSERT INTO [dbo].[InvoiceLines] (Id, InvoiceId, Description, Amount, CreatedAtUtc, CreatedBy, UpdatedAtUtc, UpdatedBy, IsDeleted, TenantId, ProductId, Name, UnitPrice, Quantity, LineDiscount, TaxRate)
VALUES
    ('6B334B36-14D2-4DD4-AD3E-21AEDB1074EE', '01D0B264-982D-42FD-91F5-E34A32F5889F', N'Unlimited users, white-label branding, API access, dedicated support manager, 99.9% SLA.', 62987.40, DATEADD(DAY, -2, @Now), 'Fatima Al-Zaabi', NULL, NULL, 0, 'BB100004-AAAA-0000-0000-000000000004', '868A70B6-7DFE-413A-9D93-0EF82D137138', N'CRM Enterprise Plan', 4999.00, 12, 0.00, 0.0500),
    ('BF8DED7F-73A5-4CE9-97B7-6F73AE15318C', '01D0B264-982D-42FD-91F5-E34A32F5889F', N'Full setup, data migration, custom fields configuration, 3 training sessions, on-site go-live support.', 7875.00, DATEADD(DAY, -2, @Now), 'Fatima Al-Zaabi', NULL, NULL, 0, 'BB100004-AAAA-0000-0000-000000000004', '1BF2244D-6872-42E6-BC0E-97CF4F15D7B6', N'Premium Implementation Package', 7500.00, 1, 0.00, 0.0500),
    ('C440A8B6-4F9E-4943-8253-E7BA289F1CC6', '01D0B264-982D-42FD-91F5-E34A32F5889F', N'Dedicated WhatsApp support line, 2hr SLA, quarterly business reviews, free minor upgrades.', 3780.00, DATEADD(DAY, -2, @Now), 'Fatima Al-Zaabi', NULL, NULL, 0, 'BB100004-AAAA-0000-0000-000000000004', '577CA65B-17D1-4863-9858-77DC16B115DD', N'Annual Support & Maintenance', 3600.00, 1, 0.00, 0.0500);

INSERT INTO [dbo].[Payments] (Id, InvoiceId, Amount, Currency, Method, Status, ProviderTxnId, PaidAtUtc, CreatedAtUtc, CreatedBy, UpdatedAtUtc, UpdatedBy, IsDeleted, TenantId, Notes)
VALUES ('1A8D3F9F-1666-415D-8E1D-84D404B421A4', '01D0B264-982D-42FD-91F5-E34A32F5889F', 74642.40, 'AED', 'BankTransfer', 'Captured', NULL, DATEADD(DAY, -1, @Now), DATEADD(DAY, -1, @Now), 'Fatima Al-Zaabi', NULL, NULL, 0, 'BB100004-AAAA-0000-0000-000000000004', N'full payment');

UPDATE [dbo].[Leads]
SET Status = 4, IsConverted = 1, ConvertedToContactId = '7B301291-783A-4FC0-9905-28F326687803',
    ConvertedToCompanyId = '1BEF179E-C9A7-4AA2-A15D-66CD184A6951', ConvertedAtUtc = DATEADD(DAY, -9, @Now),
    ConvertedBy = 'Fatima Al-Zaabi', DealId = '5AFB6ECA-9EEF-40EC-B671-D78F5BCCA016',
    UpdatedAtUtc = @Now, UpdatedBy = 'DemoSeed'
WHERE Id = 'FEED0004-0001-0000-0000-000000000001';


-- ── Deal 6: Fatima Al Hassan / Gulf Transport & Logistics Co → Negotiation ──
INSERT INTO [dbo].[Companies] (Id, Name, Country, TaxId, CreatedAtUtc, CreatedBy, UpdatedAtUtc, UpdatedBy, IsDeleted, Vertical, TenantId)
VALUES ('425F9BC5-2011-4695-A7B1-2E991BEDA157', N'Gulf Transport & Logistics Co', 'AE', NULL, @Now, 'Omar Hassan', NULL, NULL, 0, 'Logistics', 'BB100004-AAAA-0000-0000-000000000004');

INSERT INTO [dbo].[Contacts] (Id, CompanyId, FirstName, LastName, Email, Phone, LineUserId, CreatedAtUtc, CreatedBy, UpdatedAtUtc, UpdatedBy, IsDeleted, TenantId, JobTitle, Mobile, Address, City, Country, PostalCode, Notes, IsPrimary)
VALUES ('67102F7E-A98B-4518-AF5C-C9A19D883841', '425F9BC5-2011-4695-A7B1-2E991BEDA157', N'Fatima', N'Al Hassan', 'fatima@gulftransport.ae', '+971-55-333-4002', NULL, @Now, 'Omar Hassan', @Now, 'Omar Hassan', 0, 'BB100004-AAAA-0000-0000-000000000004', NULL, NULL, NULL, NULL, 'AE', NULL, NULL, 1);

INSERT INTO [dbo].[Deals] (Id, TenantId, ContactId, LeadId, Title, Description, Stage, Priority, Probability, ExpectedValue, ActualValue, ExpectedCloseDateUtc, ActualCloseDateUtc, OwnerUserId, Tags, CustomFields, CreatedAtUtc, CreatedBy, UpdatedAtUtc, UpdatedBy, IsDeleted, DeletedAtUtc, DeletedBy, CompanyId, LostReason, SourceId, Currency, VerticalId, Source)
VALUES ('C1041599-54CA-4DA6-BFCC-6A11F71233EF', 'BB100004-AAAA-0000-0000-000000000004', '67102F7E-A98B-4518-AF5C-C9A19D883841', 'FEED0004-0002-0000-0000-000000000002',
        N'Deal - Fatima Al Hassan', N'Fleet + shipment tracking CRM, comparing pricing tiers', 'Negotiation', NULL, 60, 112000.00, NULL,
        DATEADD(DAY, 15, @Now), NULL, 'DD100004-DD00-0000-0000-000000000011', NULL, NULL,
        @Now, 'Omar Hassan', @Now, 'Omar Hassan', 0, NULL, NULL, '425F9BC5-2011-4695-A7B1-2E991BEDA157', NULL, NULL, 'AED', 'EC90538D-4CCA-45E0-885C-054172F024F2', NULL);

UPDATE [dbo].[Leads]
SET Status = 4, IsConverted = 1, ConvertedToContactId = '67102F7E-A98B-4518-AF5C-C9A19D883841',
    ConvertedToCompanyId = '425F9BC5-2011-4695-A7B1-2E991BEDA157', ConvertedAtUtc = @Now,
    ConvertedBy = 'Omar Hassan', DealId = 'C1041599-54CA-4DA6-BFCC-6A11F71233EF',
    UpdatedAtUtc = @Now, UpdatedBy = 'DemoSeed'
WHERE Id = 'FEED0004-0002-0000-0000-000000000002';


-- ── Deal 7: Fatima Al-Zaabi / Abu Dhabi Corporate Services → Proposal ───
-- Owner: Omar Hassan (NOT Fatima Al-Zaabi — avoids owner/contact name collision)
INSERT INTO [dbo].[Companies] (Id, Name, Country, TaxId, CreatedAtUtc, CreatedBy, UpdatedAtUtc, UpdatedBy, IsDeleted, Vertical, TenantId)
VALUES ('75B20278-93E4-4978-89B5-D5E642BF5D4B', N'Abu Dhabi Corporate Services', 'AE', NULL, @Now, 'Omar Hassan', NULL, NULL, 0, 'Manufacturing', 'BB100004-AAAA-0000-0000-000000000004');

INSERT INTO [dbo].[Contacts] (Id, CompanyId, FirstName, LastName, Email, Phone, LineUserId, CreatedAtUtc, CreatedBy, UpdatedAtUtc, UpdatedBy, IsDeleted, TenantId, JobTitle, Mobile, Address, City, Country, PostalCode, Notes, IsPrimary)
VALUES ('90753DF6-5E5E-4BD2-9B92-8E8E62CE734C', '75B20278-93E4-4978-89B5-D5E642BF5D4B', N'Fatima', N'Al-Zaabi', 'fatima@abudhabicorp.ae', '+971 55 987 6543', NULL, @Now, 'Omar Hassan', @Now, 'Omar Hassan', 0, 'BB100004-AAAA-0000-0000-000000000004', NULL, NULL, N'Floor 12, Al Maryah Island, Abu Dhabi', NULL, 'AE', NULL, NULL, 1);

INSERT INTO [dbo].[Deals] (Id, TenantId, ContactId, LeadId, Title, Description, Stage, Priority, Probability, ExpectedValue, ActualValue, ExpectedCloseDateUtc, ActualCloseDateUtc, OwnerUserId, Tags, CustomFields, CreatedAtUtc, CreatedBy, UpdatedAtUtc, UpdatedBy, IsDeleted, DeletedAtUtc, DeletedBy, CompanyId, LostReason, SourceId, Currency, VerticalId, Source)
VALUES ('5ED997BF-778C-4170-8C3E-AF7FC2A62B3F', 'BB100004-AAAA-0000-0000-000000000004', '90753DF6-5E5E-4BD2-9B92-8E8E62CE734C', 'CCF9E235-E9E1-4DF9-8B0D-C3B6566282EF',
        N'Deal - Fatima Al-Zaabi', N'Corporate services CRM for client onboarding team', 'Proposal', NULL, 40, 8501.85, NULL,
        DATEADD(DAY, 10, @Now), NULL, 'DD100004-DD00-0000-0000-000000000011', NULL, NULL,
        @Now, 'Omar Hassan', @Now, 'Omar Hassan', 0, NULL, NULL, '75B20278-93E4-4978-89B5-D5E642BF5D4B', NULL, NULL, 'AED', '9050598B-55A0-4E74-8D21-72C26B42189E', NULL);

INSERT INTO [dbo].[Quotes] (Id, DealId, Number, IssueDateUtc, ExpiresAtUtc, Currency, Status, Subtotal, DiscountTotal, TaxTotal, GrandTotal, PaymentLinkUrl, PdfUrl, CreatedAtUtc, CreatedBy, UpdatedAtUtc, UpdatedBy, IsDeleted, TenantId, PublicLinkToken)
VALUES ('597724B8-3CF8-4817-A74F-5FEB31E82518', '5ED997BF-778C-4170-8C3E-AF7FC2A62B3F', 'QUO-0002', DATEADD(DAY, -1, @Now), DATEADD(DAY, 29, @Now), 'AED', 1,
        8097.00, 0.00, 404.85, 8501.85, NULL, NULL, DATEADD(DAY, -1, @Now), 'Omar Hassan', DATEADD(DAY, -1, @Now), 'Omar Hassan', 0, 'BB100004-AAAA-0000-0000-000000000004', NULL);

INSERT INTO [dbo].[QuoteItems] (Id, QuoteId, ProductId, Description, UnitPrice, Quantity, LineDiscount, TaxRate, CreatedAtUtc, CreatedBy, UpdatedAtUtc, UpdatedBy, IsDeleted, TenantId, Name)
VALUES
    ('76BE48DF-9F6C-447D-8D0C-54FC7BA20AD7', '597724B8-3CF8-4817-A74F-5FEB31E82518', '28EC4945-5C8C-41C5-ACCE-461D038BA0B8', N'Full CRM platform — Leads, Deals, Contacts, Pipeline. Up to 10 users. Monthly billing.', 1499.00, 3, 0.00, 0.0500, DATEADD(DAY, -1, @Now), 'Omar Hassan', NULL, NULL, 0, 'BB100004-AAAA-0000-0000-000000000004', N'CRM Professional Plan'),
    ('5F36D4A0-B0B9-4805-889A-7690FF61D72E', '597724B8-3CF8-4817-A74F-5FEB31E82518', '577CA65B-17D1-4863-9858-77DC16B115DD', N'Dedicated WhatsApp support line, 2hr SLA, quarterly business reviews, free minor upgrades.', 3600.00, 1, 0.00, 0.0500, DATEADD(DAY, -1, @Now), 'Omar Hassan', NULL, NULL, 0, 'BB100004-AAAA-0000-0000-000000000004', N'Annual Support & Maintenance');

UPDATE [dbo].[Leads]
SET Status = 4, IsConverted = 1, ConvertedToContactId = '90753DF6-5E5E-4BD2-9B92-8E8E62CE734C',
    ConvertedToCompanyId = '75B20278-93E4-4978-89B5-D5E642BF5D4B', ConvertedAtUtc = @Now,
    ConvertedBy = 'Omar Hassan', DealId = '5ED997BF-778C-4170-8C3E-AF7FC2A62B3F',
    UpdatedAtUtc = @Now, UpdatedBy = 'DemoSeed'
WHERE Id = 'CCF9E235-E9E1-4DF9-8B0D-C3B6566282EF';


-- ── Deal 8: Khalid Al-Mansouri / Dubai Tech Innovations LLC → ClosedLost ─
INSERT INTO [dbo].[Companies] (Id, Name, Country, TaxId, CreatedAtUtc, CreatedBy, UpdatedAtUtc, UpdatedBy, IsDeleted, Vertical, TenantId)
VALUES ('F0F5EB07-11B0-411A-A96C-251D70DAC146', N'Dubai Tech Innovations LLC', 'AE', NULL, @Now, 'Fatima Al-Zaabi', NULL, NULL, 0, 'Information Tech Services', 'BB100004-AAAA-0000-0000-000000000004');

INSERT INTO [dbo].[Contacts] (Id, CompanyId, FirstName, LastName, Email, Phone, LineUserId, CreatedAtUtc, CreatedBy, UpdatedAtUtc, UpdatedBy, IsDeleted, TenantId, JobTitle, Mobile, Address, City, Country, PostalCode, Notes, IsPrimary)
VALUES ('99DD37FE-E5B2-4676-8B5B-1475D692E700', 'F0F5EB07-11B0-411A-A96C-251D70DAC146', N'Khalid', N'Al-Mansouri', 'khalid@dubaitech.ae', '+971 50 123 4567', NULL, @Now, 'Fatima Al-Zaabi', @Now, 'Fatima Al-Zaabi', 0, 'BB100004-AAAA-0000-0000-000000000004', NULL, NULL, N'Office 802, Business Bay, Dubai', NULL, 'AE', NULL, NULL, 1);

INSERT INTO [dbo].[Deals] (Id, TenantId, ContactId, LeadId, Title, Description, Stage, Priority, Probability, ExpectedValue, ActualValue, ExpectedCloseDateUtc, ActualCloseDateUtc, OwnerUserId, Tags, CustomFields, CreatedAtUtc, CreatedBy, UpdatedAtUtc, UpdatedBy, IsDeleted, DeletedAtUtc, DeletedBy, CompanyId, LostReason, SourceId, Currency, VerticalId, Source)
VALUES ('718682DD-3530-463B-8074-EF971F55C7D2', 'BB100004-AAAA-0000-0000-000000000004', '99DD37FE-E5B2-4676-8B5B-1475D692E700', 'C0BDFF2B-8E9F-4FB9-BAB0-DB5E4D995C0B',
        N'Deal - Khalid Al-Mansouri', N'Tech startup evaluating CRM options for growing sales team', 'ClosedLost', NULL, 0, 45000.00, NULL,
        DATEADD(DAY, -1, @Now), DATEADD(DAY, -1, @Now), 'DD100004-DD00-0000-0000-000000000010', NULL, NULL,
        @Now, 'Fatima Al-Zaabi', @Now, 'Fatima Al-Zaabi', 0, NULL, NULL, 'F0F5EB07-11B0-411A-A96C-251D70DAC146',
        N'Budget approved for a competing platform this quarter.', NULL, 'AED', '9D900454-7206-4453-B734-E128A80B0EB9', NULL);

INSERT INTO [dbo].[Quotes] (Id, DealId, Number, IssueDateUtc, ExpiresAtUtc, Currency, Status, Subtotal, DiscountTotal, TaxTotal, GrandTotal, PaymentLinkUrl, PdfUrl, CreatedAtUtc, CreatedBy, UpdatedAtUtc, UpdatedBy, IsDeleted, TenantId, PublicLinkToken)
VALUES ('F97E6567-BAE8-4770-B6B1-B83C19574E86', '718682DD-3530-463B-8074-EF971F55C7D2', 'QUO-0003', DATEADD(DAY, -11, @Now), DATEADD(DAY, -1, @Now), 'AED', 4,
        5099.00, 0.00, 254.95, 5353.95, NULL, NULL, DATEADD(DAY, -11, @Now), 'Fatima Al-Zaabi', DATEADD(DAY, -1, @Now), 'Fatima Al-Zaabi', 0, 'BB100004-AAAA-0000-0000-000000000004', NULL);

INSERT INTO [dbo].[QuoteItems] (Id, QuoteId, ProductId, Description, UnitPrice, Quantity, LineDiscount, TaxRate, CreatedAtUtc, CreatedBy, UpdatedAtUtc, UpdatedBy, IsDeleted, TenantId, Name)
VALUES
    ('3EEA25D8-2686-487C-8031-A48564EEB1E2', 'F97E6567-BAE8-4770-B6B1-B83C19574E86', '28EC4945-5C8C-41C5-ACCE-461D038BA0B8', N'Full CRM platform — Leads, Deals, Contacts, Pipeline. Up to 10 users. Monthly billing.', 1499.00, 1, 0.00, 0.0500, DATEADD(DAY, -11, @Now), 'Fatima Al-Zaabi', NULL, NULL, 0, 'BB100004-AAAA-0000-0000-000000000004', N'CRM Professional Plan'),
    ('9A65505E-37DD-48D2-96D2-C945CE412A9E', 'F97E6567-BAE8-4770-B6B1-B83C19574E86', '577CA65B-17D1-4863-9858-77DC16B115DD', N'Dedicated WhatsApp support line, 2hr SLA, quarterly business reviews, free minor upgrades.', 3600.00, 1, 0.00, 0.0500, DATEADD(DAY, -11, @Now), 'Fatima Al-Zaabi', NULL, NULL, 0, 'BB100004-AAAA-0000-0000-000000000004', N'Annual Support & Maintenance');

UPDATE [dbo].[Leads]
SET Status = 4, IsConverted = 1, ConvertedToContactId = '99DD37FE-E5B2-4676-8B5B-1475D692E700',
    ConvertedToCompanyId = 'F0F5EB07-11B0-411A-A96C-251D70DAC146', ConvertedAtUtc = @Now,
    ConvertedBy = 'Fatima Al-Zaabi', DealId = '718682DD-3530-463B-8074-EF971F55C7D2',
    UpdatedAtUtc = @Now, UpdatedBy = 'DemoSeed'
WHERE Id = 'C0BDFF2B-8E9F-4FB9-BAB0-DB5E4D995C0B';


-- ══════════════════════════════════════════════════════════════════════
-- Verify before committing
-- ══════════════════════════════════════════════════════════════════════

SELECT t.Name AS Tenant, d.Title, d.Stage, d.ExpectedValue, d.Currency,
       q.Number AS QuoteNumber, q.Status AS QuoteStatus, q.GrandTotal,
       i.Number AS InvoiceNumber, i.Status AS InvoiceStatus, i.Total, i.Balance
FROM [dbo].[Deals] d
JOIN [dbo].[Tenants] t ON t.Id = d.TenantId
LEFT JOIN [dbo].[Quotes] q ON q.DealId = d.Id
LEFT JOIN [dbo].[Invoices] i ON i.DealId = d.Id
WHERE d.TenantId IN ('BB100001-AAAA-0000-0000-000000000001', 'BB100004-AAAA-0000-0000-000000000004')
ORDER BY t.Name, d.CreatedAtUtc;

-- ── Review the output above. If it looks right: ──────────────────────────
COMMIT TRANSACTION;

-- ── If something looks wrong, run this instead of the COMMIT above: ─────
-- ROLLBACK TRANSACTION;
