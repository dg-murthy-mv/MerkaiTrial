-- =====================================================================
-- TestData_Leads_AllTenants.sql
-- Test data for MadeeFlow Leads — India, Thailand, Philippines, UAE
--
-- Purpose: Validate currency display, lead scoring, notes/activities/reminders
-- Each tenant gets 3 leads with different completion levels:
--   Lead 1 — Full profile  (high score expected ~75-85)
--   Lead 2 — Partial profile (mid score expected ~40-55)
--   Lead 3 — Minimal profile (low score expected ~10-20)
--
-- Tenant → Country mapping (from Tenants table):
--   India       BB100001  CountryId BF5E754A  INR  ₹
--   Thailand    BB100002  CountryId 9F595396   THB  ฿
--   Philippines BB100003  CountryId 8DACB836   PHP  ₱
--   UAE         BB100004  CountryId 74FB20E3   AED  د.إ
-- =====================================================================

BEGIN TRANSACTION;
SET NOCOUNT ON;

-- =====================================================================
-- STEP 1 — Variables
-- =====================================================================

-- Tenant IDs
DECLARE @TIN   UNIQUEIDENTIFIER = 'BB100001-AAAA-0000-0000-000000000001'
DECLARE @TTH   UNIQUEIDENTIFIER = 'BB100002-AAAA-0000-0000-000000000002'
DECLARE @TPH   UNIQUEIDENTIFIER = 'BB100003-AAAA-0000-0000-000000000003'
DECLARE @TUAE  UNIQUEIDENTIFIER = 'BB100004-AAAA-0000-0000-000000000004'

-- Country IDs (from Tenants.CountryId)
DECLARE @CIN   UNIQUEIDENTIFIER = 'BF5E754A-7332-4485-8506-E75F5E713E55'
DECLARE @CTH   UNIQUEIDENTIFIER = '9F595396-9B64-42DF-9F77-56848C95C98F'
DECLARE @CPH   UNIQUEIDENTIFIER = '8DACB836-A8DC-4E49-B87E-D540D37E7719'
DECLARE @CUAE  UNIQUEIDENTIFIER = '74FB20E3-3EA0-4F26-8C0D-2C004665517A'

-- Dev user (India admin from appsettings)
DECLARE @UserIN  UNIQUEIDENTIFIER = 'DD100001-DD00-0000-0000-000000000001'

-- =====================================================================
-- STEP 2 — Sales Users (one per tenant for OwnerUserId)
-- =====================================================================

DECLARE @UserTH  UNIQUEIDENTIFIER = 'DD100002-DD00-0000-0000-000000000002'
DECLARE @UserPH  UNIQUEIDENTIFIER = 'DD100003-DD00-0000-0000-000000000003'
DECLARE @UserUAE UNIQUEIDENTIFIER = 'DD100004-DD00-0000-0000-000000000004'

IF NOT EXISTS (SELECT 1 FROM Users WHERE Id = @UserTH)
    INSERT INTO Users (Id,TenantId,FirstName,LastName,Email,Phone,JobTitle,Department,IsActive,IsTenantAdmin,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES (@UserTH,@TTH,'Somchai','Jaidee','admin@madeeflow.co.th','+66-89-123-4567','Account Manager','Sales',1,1,0,GETUTCDATE(),'seed');

IF NOT EXISTS (SELECT 1 FROM Users WHERE Id = @UserPH)
    INSERT INTO Users (Id,TenantId,FirstName,LastName,Email,Phone,JobTitle,Department,IsActive,IsTenantAdmin,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES (@UserPH,@TPH,'Maria','Santos','admin@madeeflow.ph','+63-917-123-4567','Sales Executive','Sales',1,1,0,GETUTCDATE(),'seed');

IF NOT EXISTS (SELECT 1 FROM Users WHERE Id = @UserUAE)
    INSERT INTO Users (Id,TenantId,FirstName,LastName,Email,Phone,JobTitle,Department,IsActive,IsTenantAdmin,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES (@UserUAE,@TUAE,'Ahmed','Al Rashid','admin@madeeflow.ae','+971-50-123-4567','Business Development','Sales',1,1,0,GETUTCDATE(),'seed');

PRINT '✅ Users ready';

-- =====================================================================
-- STEP 3 — LeadChannels per tenant
-- =====================================================================

DECLARE @ChIN_LinkedIn  UNIQUEIDENTIFIER = NEWID()
DECLARE @ChIN_Email     UNIQUEIDENTIFIER = NEWID()
DECLARE @ChTH_Line      UNIQUEIDENTIFIER = NEWID()
DECLARE @ChTH_Facebook  UNIQUEIDENTIFIER = NEWID()
DECLARE @ChPH_Facebook  UNIQUEIDENTIFIER = NEWID()
DECLARE @ChPH_WhatsApp  UNIQUEIDENTIFIER = NEWID()
DECLARE @ChUAE_LinkedIn UNIQUEIDENTIFIER = NEWID()
DECLARE @ChUAE_Email    UNIQUEIDENTIFIER = NEWID()

IF NOT EXISTS (SELECT 1 FROM LeadChannels WHERE TenantId=@TIN AND Name='LinkedIn')
    INSERT INTO LeadChannels(Id,TenantId,Name,IsActive,DisplayOrder,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES (@ChIN_LinkedIn,@TIN,'LinkedIn',1,1,0,GETUTCDATE(),'seed');
ELSE SELECT @ChIN_LinkedIn=Id FROM LeadChannels WHERE TenantId=@TIN AND Name='LinkedIn';

IF NOT EXISTS (SELECT 1 FROM LeadChannels WHERE TenantId=@TIN AND Name='Email')
    INSERT INTO LeadChannels(Id,TenantId,Name,IsActive,DisplayOrder,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES (@ChIN_Email,@TIN,'Email',1,2,0,GETUTCDATE(),'seed');
ELSE SELECT @ChIN_Email=Id FROM LeadChannels WHERE TenantId=@TIN AND Name='Email';

IF NOT EXISTS (SELECT 1 FROM LeadChannels WHERE TenantId=@TTH AND Name='Line')
    INSERT INTO LeadChannels(Id,TenantId,Name,IsActive,DisplayOrder,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES (@ChTH_Line,@TTH,'Line',1,1,0,GETUTCDATE(),'seed');
ELSE SELECT @ChTH_Line=Id FROM LeadChannels WHERE TenantId=@TTH AND Name='Line';

IF NOT EXISTS (SELECT 1 FROM LeadChannels WHERE TenantId=@TTH AND Name='Facebook')
    INSERT INTO LeadChannels(Id,TenantId,Name,IsActive,DisplayOrder,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES (@ChTH_Facebook,@TTH,'Facebook',1,2,0,GETUTCDATE(),'seed');
ELSE SELECT @ChTH_Facebook=Id FROM LeadChannels WHERE TenantId=@TTH AND Name='Facebook';

IF NOT EXISTS (SELECT 1 FROM LeadChannels WHERE TenantId=@TPH AND Name='Facebook')
    INSERT INTO LeadChannels(Id,TenantId,Name,IsActive,DisplayOrder,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES (@ChPH_Facebook,@TPH,'Facebook',1,1,0,GETUTCDATE(),'seed');
ELSE SELECT @ChPH_Facebook=Id FROM LeadChannels WHERE TenantId=@TPH AND Name='Facebook';

IF NOT EXISTS (SELECT 1 FROM LeadChannels WHERE TenantId=@TPH AND Name='WhatsApp')
    INSERT INTO LeadChannels(Id,TenantId,Name,IsActive,DisplayOrder,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES (@ChPH_WhatsApp,@TPH,'WhatsApp',1,2,0,GETUTCDATE(),'seed');
ELSE SELECT @ChPH_WhatsApp=Id FROM LeadChannels WHERE TenantId=@TPH AND Name='WhatsApp';

IF NOT EXISTS (SELECT 1 FROM LeadChannels WHERE TenantId=@TUAE AND Name='LinkedIn')
    INSERT INTO LeadChannels(Id,TenantId,Name,IsActive,DisplayOrder,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES (@ChUAE_LinkedIn,@TUAE,'LinkedIn',1,1,0,GETUTCDATE(),'seed');
ELSE SELECT @ChUAE_LinkedIn=Id FROM LeadChannels WHERE TenantId=@TUAE AND Name='LinkedIn';

IF NOT EXISTS (SELECT 1 FROM LeadChannels WHERE TenantId=@TUAE AND Name='Email')
    INSERT INTO LeadChannels(Id,TenantId,Name,IsActive,DisplayOrder,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES (@ChUAE_Email,@TUAE,'Email',1,2,0,GETUTCDATE(),'seed');
ELSE SELECT @ChUAE_Email=Id FROM LeadChannels WHERE TenantId=@TUAE AND Name='Email';

PRINT '✅ LeadChannels ready';

-- =====================================================================
-- STEP 4 — LeadSources per tenant
-- =====================================================================

DECLARE @SrcIN_GoogleAds UNIQUEIDENTIFIER = NEWID()
DECLARE @SrcIN_Referral  UNIQUEIDENTIFIER = NEWID()
DECLARE @SrcTH_GoogleAds UNIQUEIDENTIFIER = NEWID()
DECLARE @SrcTH_Referral  UNIQUEIDENTIFIER = NEWID()
DECLARE @SrcPH_GoogleAds UNIQUEIDENTIFIER = NEWID()
DECLARE @SrcPH_ColdCall  UNIQUEIDENTIFIER = NEWID()
DECLARE @SrcUAE_Partner  UNIQUEIDENTIFIER = NEWID()
DECLARE @SrcUAE_Direct   UNIQUEIDENTIFIER = NEWID()

IF NOT EXISTS (SELECT 1 FROM LeadSources WHERE TenantId=@TIN AND Name='Google Ads')
    INSERT INTO LeadSources(Id,TenantId,Name,IsActive,DisplayOrder,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES (@SrcIN_GoogleAds,@TIN,'Google Ads',1,1,0,GETUTCDATE(),'seed');
ELSE SELECT @SrcIN_GoogleAds=Id FROM LeadSources WHERE TenantId=@TIN AND Name='Google Ads';

IF NOT EXISTS (SELECT 1 FROM LeadSources WHERE TenantId=@TIN AND Name='Referral')
    INSERT INTO LeadSources(Id,TenantId,Name,IsActive,DisplayOrder,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES (@SrcIN_Referral,@TIN,'Referral',1,2,0,GETUTCDATE(),'seed');
ELSE SELECT @SrcIN_Referral=Id FROM LeadSources WHERE TenantId=@TIN AND Name='Referral';

IF NOT EXISTS (SELECT 1 FROM LeadSources WHERE TenantId=@TTH AND Name='Google Ads')
    INSERT INTO LeadSources(Id,TenantId,Name,IsActive,DisplayOrder,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES (@SrcTH_GoogleAds,@TTH,'Google Ads',1,1,0,GETUTCDATE(),'seed');
ELSE SELECT @SrcTH_GoogleAds=Id FROM LeadSources WHERE TenantId=@TTH AND Name='Google Ads';

IF NOT EXISTS (SELECT 1 FROM LeadSources WHERE TenantId=@TTH AND Name='Referral')
    INSERT INTO LeadSources(Id,TenantId,Name,IsActive,DisplayOrder,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES (@SrcTH_Referral,@TTH,'Referral',1,2,0,GETUTCDATE(),'seed');
ELSE SELECT @SrcTH_Referral=Id FROM LeadSources WHERE TenantId=@TTH AND Name='Referral';

IF NOT EXISTS (SELECT 1 FROM LeadSources WHERE TenantId=@TPH AND Name='Google Ads')
    INSERT INTO LeadSources(Id,TenantId,Name,IsActive,DisplayOrder,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES (@SrcPH_GoogleAds,@TPH,'Google Ads',1,1,0,GETUTCDATE(),'seed');
ELSE SELECT @SrcPH_GoogleAds=Id FROM LeadSources WHERE TenantId=@TPH AND Name='Google Ads';

IF NOT EXISTS (SELECT 1 FROM LeadSources WHERE TenantId=@TPH AND Name='Cold Call')
    INSERT INTO LeadSources(Id,TenantId,Name,IsActive,DisplayOrder,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES (@SrcPH_ColdCall,@TPH,'Cold Call',1,2,0,GETUTCDATE(),'seed');
ELSE SELECT @SrcPH_ColdCall=Id FROM LeadSources WHERE TenantId=@TPH AND Name='Cold Call';

IF NOT EXISTS (SELECT 1 FROM LeadSources WHERE TenantId=@TUAE AND Name='Partner')
    INSERT INTO LeadSources(Id,TenantId,Name,IsActive,DisplayOrder,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES (@SrcUAE_Partner,@TUAE,'Partner',1,1,0,GETUTCDATE(),'seed');
ELSE SELECT @SrcUAE_Partner=Id FROM LeadSources WHERE TenantId=@TUAE AND Name='Partner';

IF NOT EXISTS (SELECT 1 FROM LeadSources WHERE TenantId=@TUAE AND Name='Direct')
    INSERT INTO LeadSources(Id,TenantId,Name,IsActive,DisplayOrder,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES (@SrcUAE_Direct,@TUAE,'Direct',1,2,0,GETUTCDATE(),'seed');
ELSE SELECT @SrcUAE_Direct=Id FROM LeadSources WHERE TenantId=@TUAE AND Name='Direct';

PRINT '✅ LeadSources ready';

-- =====================================================================
-- STEP 5 — CompanyVerticals (shared system verticals, TenantId NULL)
-- =====================================================================

DECLARE @VertTech   UNIQUEIDENTIFIER = NEWID()
DECLARE @VertRetail UNIQUEIDENTIFIER = NEWID()
DECLARE @VertFood   UNIQUEIDENTIFIER = NEWID()
DECLARE @VertFin    UNIQUEIDENTIFIER = NEWID()

IF NOT EXISTS (SELECT 1 FROM CompanyVerticals WHERE Name='Technology' AND IsDeleted=0)
    INSERT INTO CompanyVerticals(Id,TenantId,Name,Description,IsActive,IsSystem,DisplayOrder,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES (@VertTech,NULL,'Technology','IT, SaaS, Software companies',1,1,1,0,GETUTCDATE(),'seed');
ELSE SELECT @VertTech=Id FROM CompanyVerticals WHERE Name='Technology' AND IsDeleted=0;

IF NOT EXISTS (SELECT 1 FROM CompanyVerticals WHERE Name='Retail' AND IsDeleted=0)
    INSERT INTO CompanyVerticals(Id,TenantId,Name,Description,IsActive,IsSystem,DisplayOrder,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES (@VertRetail,NULL,'Retail','Retail and e-commerce',1,1,2,0,GETUTCDATE(),'seed');
ELSE SELECT @VertRetail=Id FROM CompanyVerticals WHERE Name='Retail' AND IsDeleted=0;

IF NOT EXISTS (SELECT 1 FROM CompanyVerticals WHERE Name='Food & Beverage' AND IsDeleted=0)
    INSERT INTO CompanyVerticals(Id,TenantId,Name,Description,IsActive,IsSystem,DisplayOrder,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES (@VertFood,NULL,'Food & Beverage','Restaurants, bakeries, FMCG',1,1,3,0,GETUTCDATE(),'seed');
ELSE SELECT @VertFood=Id FROM CompanyVerticals WHERE Name='Food & Beverage' AND IsDeleted=0;

IF NOT EXISTS (SELECT 1 FROM CompanyVerticals WHERE Name='Financial Services' AND IsDeleted=0)
    INSERT INTO CompanyVerticals(Id,TenantId,Name,Description,IsActive,IsSystem,DisplayOrder,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES (@VertFin,NULL,'Financial Services','Banks, insurance, fintech',1,1,4,0,GETUTCDATE(),'seed');
ELSE SELECT @VertFin=Id FROM CompanyVerticals WHERE Name='Financial Services' AND IsDeleted=0;

PRINT '✅ CompanyVerticals ready';

-- =====================================================================
-- STEP 6 — LEAD IDs
-- =====================================================================

-- India leads
DECLARE @LIN1 UNIQUEIDENTIFIER = 'EE100001-0001-0000-0000-000000000001'  -- full profile
DECLARE @LIN2 UNIQUEIDENTIFIER = 'EE100001-0002-0000-0000-000000000002'  -- partial
DECLARE @LIN3 UNIQUEIDENTIFIER = 'EE100001-0003-0000-0000-000000000003'  -- minimal

-- Thailand leads
DECLARE @LTH1 UNIQUEIDENTIFIER = 'EE100002-0001-0000-0000-000000000001'
DECLARE @LTH2 UNIQUEIDENTIFIER = 'EE100002-0002-0000-0000-000000000002'
DECLARE @LTH3 UNIQUEIDENTIFIER = 'EE100002-0003-0000-0000-000000000003'

-- Philippines leads
DECLARE @LPH1 UNIQUEIDENTIFIER = 'EE100003-0001-0000-0000-000000000001'
DECLARE @LPH2 UNIQUEIDENTIFIER = 'EE100003-0002-0000-0000-000000000002'
DECLARE @LPH3 UNIQUEIDENTIFIER = 'EE100003-0003-0000-0000-000000000003'

-- UAE leads
DECLARE @LUAE1 UNIQUEIDENTIFIER = 'EE100004-0001-0000-0000-000000000001'
DECLARE @LUAE2 UNIQUEIDENTIFIER = 'EE100004-0002-0000-0000-000000000002'
DECLARE @LUAE3 UNIQUEIDENTIFIER = 'EE100004-0003-0000-0000-000000000003'

-- =====================================================================
-- STEP 7 — INDIA LEADS  (Currency: INR)
-- Status: 0=New, 1=Working, 2=Qualified
-- =====================================================================

-- India Lead 1: Full profile, Qualified — expected score ~75+
IF NOT EXISTS (SELECT 1 FROM Leads WHERE Id=@LIN1)
INSERT INTO Leads(Id,TenantId,FullName,Email,Phone,CompanyName,
    Address,CountryId,Currency,ChannelId,SourceId,VerticalId,
    Status,Score,EstimatedValue,OwnerUserId,Channel,Source,
    IsConverted,IsDeleted,CreatedAtUtc,CreatedBy)
VALUES(@LIN1,@TIN,'Priya Sharma','priya.sharma@techcorp.in','+91-98765-11111',
    'TechCorp Solutions Pvt Ltd',
    '42 MG Road, Bangalore, Karnataka 560001',@CIN,'INR',
    @ChIN_LinkedIn,@SrcIN_GoogleAds,@VertTech,
    2,0,850000.00,CAST(@UserIN AS NVARCHAR(64)),0,'Dynamic',
    0,0,DATEADD(DAY,-15,GETUTCDATE()),'seed');

-- India Lead 2: Partial profile, Working — expected score ~45+
IF NOT EXISTS (SELECT 1 FROM Leads WHERE Id=@LIN2)
INSERT INTO Leads(Id,TenantId,FullName,Email,Phone,CompanyName,
    CountryId,Currency,ChannelId,SourceId,
    Status,Score,EstimatedValue,OwnerUserId,Channel,Source,
    IsConverted,IsDeleted,CreatedAtUtc,CreatedBy)
VALUES(@LIN2,@TIN,'Rahul Mehta','rahul.mehta@gmail.com','+91-87654-22222',
    'Mehta Retail Stores',
    @CIN,'INR',@ChIN_Email,@SrcIN_Referral,
    1,0,150000.00,CAST(@UserIN AS NVARCHAR(64)),0,'Dynamic',
    0,0,DATEADD(DAY,-7,GETUTCDATE()),'seed');

-- India Lead 3: Minimal, New — expected score ~10
IF NOT EXISTS (SELECT 1 FROM Leads WHERE Id=@LIN3)
INSERT INTO Leads(Id,TenantId,FullName,Email,
    CountryId,Currency,
    Status,Score,OwnerUserId,Channel,Source,
    IsConverted,IsDeleted,CreatedAtUtc,CreatedBy)
VALUES(@LIN3,@TIN,'Anita Patel','anita.patel@yahoo.in',
    @CIN,'INR',
    0,0,CAST(@UserIN AS NVARCHAR(64)),0,'widget',
    0,0,DATEADD(DAY,-2,GETUTCDATE()),'seed');

PRINT '✅ India Leads inserted';

-- =====================================================================
-- STEP 8 — THAILAND LEADS  (Currency: THB)
-- =====================================================================

-- Thailand Lead 1: Full profile, Qualified
IF NOT EXISTS (SELECT 1 FROM Leads WHERE Id=@LTH1)
INSERT INTO Leads(Id,TenantId,FullName,Email,Phone,CompanyName,
    Address,CountryId,Currency,ChannelId,SourceId,VerticalId,
    Status,Score,EstimatedValue,OwnerUserId,Channel,Source,
    IsConverted,IsDeleted,CreatedAtUtc,CreatedBy)
VALUES(@LTH1,@TTH,'Somying Charoenwong','somying@thairetail.co.th','+66-81-234-5678',
    'Thai Retail Group Co. Ltd',
    '123 Sukhumvit Rd, Bangkok 10110',@CTH,'THB',
    @ChTH_Line,@SrcTH_GoogleAds,@VertRetail,
    2,0,320000.00,CAST(@UserTH AS NVARCHAR(64)),0,'Dynamic',
    0,0,DATEADD(DAY,-12,GETUTCDATE()),'seed');

-- Thailand Lead 2: Partial, Working
IF NOT EXISTS (SELECT 1 FROM Leads WHERE Id=@LTH2)
INSERT INTO Leads(Id,TenantId,FullName,Email,Phone,
    CountryId,Currency,ChannelId,SourceId,VerticalId,
    Status,Score,EstimatedValue,OwnerUserId,Channel,Source,
    IsConverted,IsDeleted,CreatedAtUtc,CreatedBy)
VALUES(@LTH2,@TTH,'Krit Phongphan','krit.p@foodbiz.th','+66-89-876-5432',
    NULL,@CTH,'THB',@ChTH_Facebook,@SrcTH_Referral,@VertFood,
    1,0,75000.00,CAST(@UserTH AS NVARCHAR(64)),0,'Dynamic',
    0,0,DATEADD(DAY,-5,GETUTCDATE()),'seed');

-- Thailand Lead 3: Minimal, New
IF NOT EXISTS (SELECT 1 FROM Leads WHERE Id=@LTH3)
INSERT INTO Leads(Id,TenantId,FullName,
    CountryId,Currency,
    Status,Score,OwnerUserId,Channel,Source,
    IsConverted,IsDeleted,CreatedAtUtc,CreatedBy)
VALUES(@LTH3,@TTH,'Nattaporn Srisuk',
    @CTH,'THB',
    0,0,CAST(@UserTH AS NVARCHAR(64)),0,'widget',
    0,0,DATEADD(DAY,-1,GETUTCDATE()),'seed');

PRINT '✅ Thailand Leads inserted';

-- =====================================================================
-- STEP 9 — PHILIPPINES LEADS  (Currency: PHP)
-- =====================================================================

-- Philippines Lead 1: Full profile, Qualified
IF NOT EXISTS (SELECT 1 FROM Leads WHERE Id=@LPH1)
INSERT INTO Leads(Id,TenantId,FullName,Email,Phone,CompanyName,
    Address,CountryId,Currency,ChannelId,SourceId,VerticalId,
    Status,Score,EstimatedValue,OwnerUserId,Channel,Source,
    IsConverted,IsDeleted,CreatedAtUtc,CreatedBy)
VALUES(@LPH1,@TPH,'Jose Reyes','jose.reyes@fintech.ph','+63-917-111-2222',
    'PH FinTech Solutions Inc',
    'Unit 12B BGC Tower, Taguig City, Metro Manila',@CPH,'PHP',
    @ChPH_Facebook,@SrcPH_GoogleAds,@VertFin,
    2,0,1200000.00,CAST(@UserPH AS NVARCHAR(64)),0,'Dynamic',
    0,0,DATEADD(DAY,-20,GETUTCDATE()),'seed');

-- Philippines Lead 2: Partial, Working
IF NOT EXISTS (SELECT 1 FROM Leads WHERE Id=@LPH2)
INSERT INTO Leads(Id,TenantId,FullName,Email,Phone,CompanyName,
    CountryId,Currency,ChannelId,SourceId,
    Status,Score,EstimatedValue,OwnerUserId,Channel,Source,
    IsConverted,IsDeleted,CreatedAtUtc,CreatedBy)
VALUES(@LPH2,@TPH,'Maria Clara Dizon','mclara@retailph.com','+63-918-333-4444',
    'Dizon Retail Group',
    @CPH,'PHP',@ChPH_WhatsApp,@SrcPH_ColdCall,
    1,0,450000.00,CAST(@UserPH AS NVARCHAR(64)),0,'Dynamic',
    0,0,DATEADD(DAY,-9,GETUTCDATE()),'seed');

-- Philippines Lead 3: Minimal, New
IF NOT EXISTS (SELECT 1 FROM Leads WHERE Id=@LPH3)
INSERT INTO Leads(Id,TenantId,FullName,Phone,
    CountryId,Currency,
    Status,Score,OwnerUserId,Channel,Source,
    IsConverted,IsDeleted,CreatedAtUtc,CreatedBy)
VALUES(@LPH3,@TPH,'Rodrigo Santos','+63-920-555-6666',
    @CPH,'PHP',
    0,0,CAST(@UserPH AS NVARCHAR(64)),0,'widget',
    0,0,DATEADD(DAY,-3,GETUTCDATE()),'seed');

PRINT '✅ Philippines Leads inserted';

-- =====================================================================
-- STEP 10 — UAE LEADS  (Currency: AED)
-- =====================================================================

-- UAE Lead 1: Full profile, Qualified
IF NOT EXISTS (SELECT 1 FROM Leads WHERE Id=@LUAE1)
INSERT INTO Leads(Id,TenantId,FullName,Email,Phone,CompanyName,
    Address,CountryId,Currency,ChannelId,SourceId,VerticalId,
    Status,Score,EstimatedValue,OwnerUserId,Channel,Source,
    IsConverted,IsDeleted,CreatedAtUtc,CreatedBy)
VALUES(@LUAE1,@TUAE,'Mohammed Al Farsi','m.alfarsi@dubaitech.ae','+971-50-111-2222',
    'Dubai Tech Innovations LLC',
    'Office 401, Dubai Silicon Oasis, Dubai',@CUAE,'AED',
    @ChUAE_LinkedIn,@SrcUAE_Partner,@VertTech,
    2,0,280000.00,CAST(@UserUAE AS NVARCHAR(64)),0,'Dynamic',
    0,0,DATEADD(DAY,-18,GETUTCDATE()),'seed');

-- UAE Lead 2: Partial, Working
IF NOT EXISTS (SELECT 1 FROM Leads WHERE Id=@LUAE2)
INSERT INTO Leads(Id,TenantId,FullName,Email,Phone,CompanyName,
    CountryId,Currency,ChannelId,SourceId,VerticalId,
    Status,Score,EstimatedValue,OwnerUserId,Channel,Source,
    IsConverted,IsDeleted,CreatedAtUtc,CreatedBy)
VALUES(@LUAE2,@TUAE,'Fatima Al Hassan','fatima@trading.ae','+971-55-333-4444',
    'Al Hassan Trading Co',
    @CUAE,'AED',@ChUAE_Email,@SrcUAE_Direct,@VertRetail,
    1,0,95000.00,CAST(@UserUAE AS NVARCHAR(64)),0,'Dynamic',
    0,0,DATEADD(DAY,-6,GETUTCDATE()),'seed');

-- UAE Lead 3: Minimal, New
IF NOT EXISTS (SELECT 1 FROM Leads WHERE Id=@LUAE3)
INSERT INTO Leads(Id,TenantId,FullName,Email,
    CountryId,Currency,
    Status,Score,OwnerUserId,Channel,Source,
    IsConverted,IsDeleted,CreatedAtUtc,CreatedBy)
VALUES(@LUAE3,@TUAE,'Khalid Mansoor','khalid.m@gmail.com',
    @CUAE,'AED',
    0,0,CAST(@UserUAE AS NVARCHAR(64)),0,'widget',
    0,0,GETUTCDATE(),'seed');

PRINT '✅ UAE Leads inserted';

-- =====================================================================
-- STEP 11 — LEAD NOTES (2 per full/partial lead, 0 for minimal)
-- =====================================================================

-- India Lead 1 notes
IF NOT EXISTS (SELECT 1 FROM LeadNotes WHERE LeadId=@LIN1 AND IsDeleted=0)
BEGIN
    INSERT INTO LeadNotes(Id,LeadId,TenantId,Note,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LIN1,@TIN,'Had a detailed call with Priya. She is evaluating 3 CRM vendors. Key requirement: multi-currency support and API integration with Tally. Decision in 2 weeks.',0,DATEADD(DAY,-13,GETUTCDATE()),'seed');
    INSERT INTO LeadNotes(Id,LeadId,TenantId,Note,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LIN1,@TIN,'Sent product demo recording and pricing PDF. She confirmed budget is approved at INR 8.5L. Waiting for her to loop in the IT team.',0,DATEADD(DAY,-8,GETUTCDATE()),'seed');
END

-- India Lead 2 notes
IF NOT EXISTS (SELECT 1 FROM LeadNotes WHERE LeadId=@LIN2 AND IsDeleted=0)
    INSERT INTO LeadNotes(Id,LeadId,TenantId,Note,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LIN2,@TIN,'Rahul came through referral from existing client. Runs 5 retail stores in Mumbai. Interested in contacts and pipeline modules only for now.',0,DATEADD(DAY,-6,GETUTCDATE()),'seed');

-- Thailand Lead 1 notes
IF NOT EXISTS (SELECT 1 FROM LeadNotes WHERE LeadId=@LTH1 AND IsDeleted=0)
BEGIN
    INSERT INTO LeadNotes(Id,LeadId,TenantId,Note,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LTH1,@TTH,'Somying manages 8 retail outlets in BKK. Very interested in the pipeline board feature. Requested Thai language support — noted for roadmap.',0,DATEADD(DAY,-10,GETUTCDATE()),'seed');
    INSERT INTO LeadNotes(Id,LeadId,TenantId,Note,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LTH1,@TTH,'Follow-up call completed. Demo went well. She asked about data export to Excel — confirmed this is available. Sending proposal tomorrow.',0,DATEADD(DAY,-4,GETUTCDATE()),'seed');
END

-- Thailand Lead 2 notes
IF NOT EXISTS (SELECT 1 FROM LeadNotes WHERE LeadId=@LTH2 AND IsDeleted=0)
    INSERT INTO LeadNotes(Id,LeadId,TenantId,Note,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LTH2,@TTH,'Krit runs a chain of restaurants. Looking for something simple to track suppliers and orders. May need custom fields for food supplier contacts.',0,DATEADD(DAY,-4,GETUTCDATE()),'seed');

-- Philippines Lead 1 notes
IF NOT EXISTS (SELECT 1 FROM LeadNotes WHERE LeadId=@LPH1 AND IsDeleted=0)
BEGIN
    INSERT INTO LeadNotes(Id,LeadId,TenantId,Note,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LPH1,@TPH,'Jose is the CTO at PH FinTech. Very technical — asked about REST API docs and webhook support. Sent API documentation link. Strong buying signal.',0,DATEADD(DAY,-18,GETUTCDATE()),'seed');
    INSERT INTO LeadNotes(Id,LeadId,TenantId,Note,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LPH1,@TPH,'Second call done. Jose confirmed they want Professional plan for 10 users. Contract review underway with their legal team. Expected close in 5 days.',0,DATEADD(DAY,-7,GETUTCDATE()),'seed');
END

-- Philippines Lead 2 notes
IF NOT EXISTS (SELECT 1 FROM LeadNotes WHERE LeadId=@LPH2 AND IsDeleted=0)
    INSERT INTO LeadNotes(Id,LeadId,TenantId,Note,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LPH2,@TPH,'Maria contacted via WhatsApp. Has 3 retail branches in Cebu. Current system is spreadsheets — clear upgrade candidate. Need to schedule demo.',0,DATEADD(DAY,-8,GETUTCDATE()),'seed');

-- UAE Lead 1 notes
IF NOT EXISTS (SELECT 1 FROM LeadNotes WHERE LeadId=@LUAE1 AND IsDeleted=0)
BEGIN
    INSERT INTO LeadNotes(Id,LeadId,TenantId,Note,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LUAE1,@TUAE,'Met Mohammed at GITEX. Very impressed with the multi-tenant architecture. His firm serves 50+ enterprise clients — potential white-label partner deal.',0,DATEADD(DAY,-16,GETUTCDATE()),'seed');
    INSERT INTO LeadNotes(Id,LeadId,TenantId,Note,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LUAE1,@TUAE,'Partner onboarding call held. Discussed AED pricing and white-label branding options. Legal reviewing partner agreement. Very high value deal.',0,DATEADD(DAY,-5,GETUTCDATE()),'seed');
END

-- UAE Lead 2 notes
IF NOT EXISTS (SELECT 1 FROM LeadNotes WHERE LeadId=@LUAE2 AND IsDeleted=0)
    INSERT INTO LeadNotes(Id,LeadId,TenantId,Note,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LUAE2,@TUAE,'Fatima manages B2B trading operations. Wants CRM to track her 200+ wholesale buyers. Needs Arabic language support — flagged for Phase 2.',0,DATEADD(DAY,-5,GETUTCDATE()),'seed');

PRINT '✅ LeadNotes inserted';

-- =====================================================================
-- STEP 12 — LEAD ACTIVITIES (2 per full lead, 1 per partial)
-- ActivityType: Call | Meeting | Email | Demo | Follow-up
-- =====================================================================

-- India Lead 1
IF NOT EXISTS (SELECT 1 FROM LeadActivities WHERE LeadId=@LIN1 AND IsDeleted=0)
BEGIN
    INSERT INTO LeadActivities(Id,LeadId,TenantId,ActivityType,Subject,Description,Duration,ActivityDate,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LIN1,@TIN,'Call','Discovery call — Priya Sharma','Discussed current pain points with their existing CRM. 45 min call. Very engaged.',45,DATEADD(DAY,-14,GETUTCDATE()),0,DATEADD(DAY,-14,GETUTCDATE()),'seed');
    INSERT INTO LeadActivities(Id,LeadId,TenantId,ActivityType,Subject,Description,Duration,ActivityDate,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LIN1,@TIN,'Demo','Product demo — TechCorp Solutions','Live demo of Pipeline + Lead modules. Shown multi-currency INR/USD. Very positive response.',60,DATEADD(DAY,-9,GETUTCDATE()),0,DATEADD(DAY,-9,GETUTCDATE()),'seed');
END

-- India Lead 2
IF NOT EXISTS (SELECT 1 FROM LeadActivities WHERE LeadId=@LIN2 AND IsDeleted=0)
    INSERT INTO LeadActivities(Id,LeadId,TenantId,ActivityType,Subject,Description,Duration,ActivityDate,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LIN2,@TIN,'Call','Intro call — Rahul Mehta','Short intro call. He confirmed he was referred by Kapoor Enterprises. Interested but timeline not clear yet.',20,DATEADD(DAY,-6,GETUTCDATE()),0,DATEADD(DAY,-6,GETUTCDATE()),'seed');

-- Thailand Lead 1
IF NOT EXISTS (SELECT 1 FROM LeadActivities WHERE LeadId=@LTH1 AND IsDeleted=0)
BEGIN
    INSERT INTO LeadActivities(Id,LeadId,TenantId,ActivityType,Subject,Description,Duration,ActivityDate,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LTH1,@TTH,'Meeting','On-site visit — Thai Retail Group','Visited their Sukhumvit office. Met Somying and her team. Reviewed their current workflow.',90,DATEADD(DAY,-11,GETUTCDATE()),0,DATEADD(DAY,-11,GETUTCDATE()),'seed');
    INSERT INTO LeadActivities(Id,LeadId,TenantId,ActivityType,Subject,Description,Duration,ActivityDate,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LTH1,@TTH,'Demo','Online demo — Pipeline board','Demonstrated the Kanban board. She loved the drag-and-drop stage changes. THB values displayed correctly.',45,DATEADD(DAY,-5,GETUTCDATE()),0,DATEADD(DAY,-5,GETUTCDATE()),'seed');
END

-- Thailand Lead 2
IF NOT EXISTS (SELECT 1 FROM LeadActivities WHERE LeadId=@LTH2 AND IsDeleted=0)
    INSERT INTO LeadActivities(Id,LeadId,TenantId,ActivityType,Subject,Description,Duration,ActivityDate,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LTH2,@TTH,'Call','WhatsApp call — Krit Phongphan','Discussed requirements over Line call. Restaurant chain has 4 branches. Needs mobile-friendly UI.',30,DATEADD(DAY,-4,GETUTCDATE()),0,DATEADD(DAY,-4,GETUTCDATE()),'seed');

-- Philippines Lead 1
IF NOT EXISTS (SELECT 1 FROM LeadActivities WHERE LeadId=@LPH1 AND IsDeleted=0)
BEGIN
    INSERT INTO LeadActivities(Id,LeadId,TenantId,ActivityType,Subject,Description,Duration,ActivityDate,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LPH1,@TPH,'Call','Technical call — Jose Reyes','Deep dive into API capabilities. He reviewed our REST docs live during call. Very technical and engaged.',60,DATEADD(DAY,-17,GETUTCDATE()),0,DATEADD(DAY,-17,GETUTCDATE()),'seed');
    INSERT INTO LeadActivities(Id,LeadId,TenantId,ActivityType,Subject,Description,Duration,ActivityDate,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LPH1,@TPH,'Meeting','Contract negotiation — PH FinTech','Zoom meeting with Jose + their CFO. Agreed on Professional plan. PHP 1.2M annual. Signing next week.',45,DATEADD(DAY,-8,GETUTCDATE()),0,DATEADD(DAY,-8,GETUTCDATE()),'seed');
END

-- Philippines Lead 2
IF NOT EXISTS (SELECT 1 FROM LeadActivities WHERE LeadId=@LPH2 AND IsDeleted=0)
    INSERT INTO LeadActivities(Id,LeadId,TenantId,ActivityType,Subject,Description,Duration,ActivityDate,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LPH2,@TPH,'Demo','Online demo — Dizon Retail','Showed contacts + company modules. She was impressed by bulk import. Needs to discuss with her partner.',40,DATEADD(DAY,-7,GETUTCDATE()),0,DATEADD(DAY,-7,GETUTCDATE()),'seed');

-- UAE Lead 1
IF NOT EXISTS (SELECT 1 FROM LeadActivities WHERE LeadId=@LUAE1 AND IsDeleted=0)
BEGIN
    INSERT INTO LeadActivities(Id,LeadId,TenantId,ActivityType,Subject,Description,Duration,ActivityDate,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LUAE1,@TUAE,'Meeting','GITEX meeting — Mohammed Al Farsi','In-person meeting at GITEX booth. 30 min discussion. He took full brochure and visited our live demo terminal.',30,DATEADD(DAY,-17,GETUTCDATE()),0,DATEADD(DAY,-17,GETUTCDATE()),'seed');
    INSERT INTO LeadActivities(Id,LeadId,TenantId,ActivityType,Subject,Description,Duration,ActivityDate,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LUAE1,@TUAE,'Call','Partner deal structure call','Discussed white-label pricing in AED. 20% revenue share model proposed. He will revert after internal review.',60,DATEADD(DAY,-6,GETUTCDATE()),0,DATEADD(DAY,-6,GETUTCDATE()),'seed');
END

-- UAE Lead 2
IF NOT EXISTS (SELECT 1 FROM LeadActivities WHERE LeadId=@LUAE2 AND IsDeleted=0)
    INSERT INTO LeadActivities(Id,LeadId,TenantId,ActivityType,Subject,Description,Duration,ActivityDate,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LUAE2,@TUAE,'Email','Proposal sent — Al Hassan Trading','Sent detailed proposal with AED 95,000 annual pricing. Included ROI calculator. Awaiting response.',0,DATEADD(DAY,-4,GETUTCDATE()),0,DATEADD(DAY,-4,GETUTCDATE()),'seed');

PRINT '✅ LeadActivities inserted';

-- =====================================================================
-- STEP 13 — LEAD REMINDERS (1-2 per active lead)
-- IsCompleted: 0=pending, 1=done
-- =====================================================================

-- India Lead 1 — upcoming follow-up
IF NOT EXISTS (SELECT 1 FROM LeadReminders WHERE LeadId=@LIN1 AND IsDeleted=0)
BEGIN
    INSERT INTO LeadReminders(Id,LeadId,TenantId,Title,Description,ReminderDate,IsCompleted,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LIN1,@TIN,'Follow up on IT team decision',
        'Priya said IT team will review by this date. Call to confirm go-ahead.',
        DATEADD(DAY,2,GETUTCDATE()),0,0,DATEADD(DAY,-8,GETUTCDATE()),'seed');
    INSERT INTO LeadReminders(Id,LeadId,TenantId,Title,Description,ReminderDate,IsCompleted,CompletedAtUtc,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LIN1,@TIN,'Send pricing PDF',
        'Completed — pricing PDF sent to priya.sharma@techcorp.in',
        DATEADD(DAY,-8,GETUTCDATE()),1,DATEADD(DAY,-8,GETUTCDATE()),0,DATEADD(DAY,-13,GETUTCDATE()),'seed');
END

-- India Lead 2
IF NOT EXISTS (SELECT 1 FROM LeadReminders WHERE LeadId=@LIN2 AND IsDeleted=0)
    INSERT INTO LeadReminders(Id,LeadId,TenantId,Title,Description,ReminderDate,IsCompleted,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LIN2,@TIN,'Schedule product demo',
        'Rahul asked for a full demo next week. Confirm timing.',
        DATEADD(DAY,3,GETUTCDATE()),0,0,DATEADD(DAY,-6,GETUTCDATE()),'seed');

-- Thailand Lead 1
IF NOT EXISTS (SELECT 1 FROM LeadReminders WHERE LeadId=@LTH1 AND IsDeleted=0)
BEGIN
    INSERT INTO LeadReminders(Id,LeadId,TenantId,Title,Description,ReminderDate,IsCompleted,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LTH1,@TTH,'Send Thai proposal',
        'Somying requested proposal in Thai Baht. Prepare and send THB-denominated doc.',
        DATEADD(DAY,1,GETUTCDATE()),0,0,DATEADD(DAY,-5,GETUTCDATE()),'seed');
    INSERT INTO LeadReminders(Id,LeadId,TenantId,Title,Description,ReminderDate,IsCompleted,CompletedAtUtc,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LTH1,@TTH,'Confirm demo recording sent',
        'Completed — demo recording sent via Line.',
        DATEADD(DAY,-10,GETUTCDATE()),1,DATEADD(DAY,-10,GETUTCDATE()),0,DATEADD(DAY,-12,GETUTCDATE()),'seed');
END

-- Philippines Lead 1
IF NOT EXISTS (SELECT 1 FROM LeadReminders WHERE LeadId=@LPH1 AND IsDeleted=0)
    INSERT INTO LeadReminders(Id,LeadId,TenantId,Title,Description,ReminderDate,IsCompleted,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LPH1,@TPH,'Contract signing follow-up',
        'Jose said legal review done by this date. Call to confirm contract signing.',
        DATEADD(DAY,5,GETUTCDATE()),0,0,DATEADD(DAY,-8,GETUTCDATE()),'seed');

-- Philippines Lead 2
IF NOT EXISTS (SELECT 1 FROM LeadReminders WHERE LeadId=@LPH2 AND IsDeleted=0)
    INSERT INTO LeadReminders(Id,LeadId,TenantId,Title,Description,ReminderDate,IsCompleted,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LPH2,@TPH,'Follow up after partner discussion',
        'Maria said she needs to discuss with business partner. Check if demo can be rescheduled.',
        DATEADD(DAY,4,GETUTCDATE()),0,0,DATEADD(DAY,-7,GETUTCDATE()),'seed');

-- UAE Lead 1
IF NOT EXISTS (SELECT 1 FROM LeadReminders WHERE LeadId=@LUAE1 AND IsDeleted=0)
BEGIN
    INSERT INTO LeadReminders(Id,LeadId,TenantId,Title,Description,ReminderDate,IsCompleted,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LUAE1,@TUAE,'Partner agreement review deadline',
        'Mohammed said internal review completes today. Call to get sign-off.',
        DATEADD(DAY,1,GETUTCDATE()),0,0,DATEADD(DAY,-6,GETUTCDATE()),'seed');
    INSERT INTO LeadReminders(Id,LeadId,TenantId,Title,Description,ReminderDate,IsCompleted,CompletedAtUtc,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LUAE1,@TUAE,'Send GITEX follow-up email',
        'Completed — sent follow-up email with brochure and pricing.',
        DATEADD(DAY,-15,GETUTCDATE()),1,DATEADD(DAY,-15,GETUTCDATE()),0,DATEADD(DAY,-16,GETUTCDATE()),'seed');
END

-- UAE Lead 2
IF NOT EXISTS (SELECT 1 FROM LeadReminders WHERE LeadId=@LUAE2 AND IsDeleted=0)
    INSERT INTO LeadReminders(Id,LeadId,TenantId,Title,Description,ReminderDate,IsCompleted,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LUAE2,@TUAE,'Follow up on AED proposal',
        'Proposal sent 4 days ago. No response yet. Call Fatima to check.',
        DATEADD(DAY,2,GETUTCDATE()),0,0,DATEADD(DAY,-4,GETUTCDATE()),'seed');

PRINT '✅ LeadReminders inserted';

-- =====================================================================
-- STEP 14 — VERIFY: Expected scores after RecalculateAsync runs
-- =====================================================================

SELECT
    T.Name                          AS Tenant,
    L.FullName                      AS Lead,
    L.Currency,
    L.EstimatedValue,
    CASE L.Status WHEN 0 THEN 'New' WHEN 1 THEN 'Working' WHEN 2 THEN 'Qualified' ELSE '?' END AS Status,
    L.Score                         AS CurrentScore,
    -- Expected score calculation (matches LeadScoringService.Calculate)
    (
        CASE WHEN L.Email       IS NOT NULL THEN 10 ELSE 0 END +
        CASE WHEN L.Phone       IS NOT NULL THEN 10 ELSE 0 END +
        CASE WHEN L.CompanyName IS NOT NULL THEN 10 ELSE 0 END +
        CASE WHEN L.CountryId   IS NOT NULL THEN 5  ELSE 0 END +
        CASE WHEN L.VerticalId  IS NOT NULL THEN 5  ELSE 0 END +
        CASE WHEN ISNULL(L.EstimatedValue,0) > 0 THEN 5 ELSE 0 END +
        CASE WHEN L.ChannelId   IS NOT NULL THEN 3  ELSE 0 END +
        CASE WHEN L.SourceId    IS NOT NULL THEN 2  ELSE 0 END +
        -- Notes engagement (max 15)
        CASE WHEN (SELECT COUNT(*) FROM LeadNotes N WHERE N.LeadId=L.Id AND N.IsDeleted=0) >= 3 THEN 15
             WHEN (SELECT COUNT(*) FROM LeadNotes N WHERE N.LeadId=L.Id AND N.IsDeleted=0) = 2 THEN 10
             WHEN (SELECT COUNT(*) FROM LeadNotes N WHERE N.LeadId=L.Id AND N.IsDeleted=0) = 1 THEN 5
             ELSE 0 END +
        -- Activity engagement (max 20)
        CASE WHEN (SELECT COUNT(*) FROM LeadActivities A WHERE A.LeadId=L.Id AND A.IsDeleted=0) >= 4 THEN 20
             WHEN (SELECT COUNT(*) FROM LeadActivities A WHERE A.LeadId=L.Id AND A.IsDeleted=0) = 3 THEN 15
             WHEN (SELECT COUNT(*) FROM LeadActivities A WHERE A.LeadId=L.Id AND A.IsDeleted=0) = 2 THEN 10
             WHEN (SELECT COUNT(*) FROM LeadActivities A WHERE A.LeadId=L.Id AND A.IsDeleted=0) = 1 THEN 5
             ELSE 0 END +
        -- Status
        CASE L.Status WHEN 1 THEN 5 WHEN 2 THEN 15 ELSE 0 END
    )                               AS ExpectedScore,
    (SELECT COUNT(*) FROM LeadNotes     N WHERE N.LeadId=L.Id AND N.IsDeleted=0) AS Notes,
    (SELECT COUNT(*) FROM LeadActivities A WHERE A.LeadId=L.Id AND A.IsDeleted=0) AS Activities,
    (SELECT COUNT(*) FROM LeadReminders R WHERE R.LeadId=L.Id AND R.IsDeleted=0 AND R.IsCompleted=0) AS PendingReminders
FROM Leads L
JOIN Tenants T ON T.Id = L.TenantId
WHERE L.IsDeleted = 0
  AND L.TenantId IN (@TIN,@TTH,@TPH,@TUAE)
  AND L.CreatedBy = 'seed'
ORDER BY T.Name, ExpectedScore DESC;

COMMIT;
PRINT '';
PRINT '✅ All test data inserted. Run RecalculateAsync or update scores manually.';
PRINT '   Scores will auto-update when you open each lead in the UI (via handler calls).';
PRINT '   Or run: UPDATE Leads SET Score=0 WHERE CreatedBy=''seed'' to force recalc on next load.';
