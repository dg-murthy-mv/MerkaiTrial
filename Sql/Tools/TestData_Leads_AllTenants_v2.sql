-- =====================================================================
-- TestData_Leads_AllTenants_v2.sql
-- Uses REAL existing IDs — no inserts for Users/Channels/Sources/Verticals
-- 3 leads per tenant × 4 tenants = 12 leads total
-- Each lead has notes, activities, reminders
-- Score=0 on insert — will auto-recalculate when opened in UI
-- =====================================================================

BEGIN TRANSACTION;
SET NOCOUNT ON;

-- =====================================================================
-- REAL IDs from your DB (no inserts needed)
-- =====================================================================

-- ── Tenants ───────────────────────────────────────────────────────────
DECLARE @TIN   UNIQUEIDENTIFIER = 'BB100001-AAAA-0000-0000-000000000001'  -- India
DECLARE @TTH   UNIQUEIDENTIFIER = 'BB100002-AAAA-0000-0000-000000000002'  -- Thailand
DECLARE @TPH   UNIQUEIDENTIFIER = 'BB100003-AAAA-0000-0000-000000000003'  -- Philippines
DECLARE @TUAE  UNIQUEIDENTIFIER = 'BB100004-AAAA-0000-0000-000000000004'  -- UAE

-- ── Countries (from Tenants.CountryId) ───────────────────────────────
DECLARE @CIN   UNIQUEIDENTIFIER = 'BF5E754A-7332-4485-8506-E75F5E713E55'
DECLARE @CTH   UNIQUEIDENTIFIER = '9F595396-9B64-42DF-9F77-56848C95C98F'
DECLARE @CPH   UNIQUEIDENTIFIER = '8DACB836-A8DC-4E49-B87E-D540D37E7719'
DECLARE @CUAE  UNIQUEIDENTIFIER = '74FB20E3-3EA0-4F26-8C0D-2C004665517A'

-- ── Users (real seed users) ───────────────────────────────────────────
DECLARE @UIN   NVARCHAR(64) = 'DD100001-DD00-0000-0000-000000000001'  -- Rajesh Sharma
DECLARE @UTH   NVARCHAR(64) = 'DD100002-DD00-0000-0000-000000000002'  -- Somchai Wiriya
DECLARE @UPH   NVARCHAR(64) = 'DD100003-DD00-0000-0000-000000000003'  -- Maria Santos
DECLARE @UUAE  NVARCHAR(64) = 'DD100004-DD00-0000-0000-000000000004'  -- Ahmed Al-Rashidi

-- ── CompanyVerticals (real existing) ─────────────────────────────────
DECLARE @VBanking  UNIQUEIDENTIFIER = '7B4F9504-8E2E-448D-B669-00F636AC71F4'
DECLARE @VLogistic UNIQUEIDENTIFIER = 'EC90538D-4CCA-45E0-885C-054172F024F2'
DECLARE @VHealth   UNIQUEIDENTIFIER = 'B1926BA0-09E8-4802-AB9A-6F9D5A57B56C'
DECLARE @VManuf    UNIQUEIDENTIFIER = '9050598B-55A0-4E74-8D21-72C26B42189E'
DECLARE @VEngr     UNIQUEIDENTIFIER = '80AC7DF3-3AB4-4B95-81DB-9BED0C1C0820'
DECLARE @VIT       UNIQUEIDENTIFIER = '9D900454-7206-4453-B734-E128A80B0EB9'

-- ── LeadChannels (real existing) ─────────────────────────────────────
-- India
DECLARE @ChIN_Email     UNIQUEIDENTIFIER = 'AB110001-AB00-0000-0000-000000000001'
DECLARE @ChIN_WhatsApp  UNIQUEIDENTIFIER = 'AB110002-AB00-0000-0000-000000000002'
DECLARE @ChIN_LinkedIn  UNIQUEIDENTIFIER = 'AB110006-AB00-0000-0000-000000000006'
-- Thailand
DECLARE @ChTH_LINE      UNIQUEIDENTIFIER = 'AB120001-AB00-0000-0000-000000000001'
DECLARE @ChTH_Facebook  UNIQUEIDENTIFIER = 'AB120004-AB00-0000-0000-000000000004'
DECLARE @ChTH_InPerson  UNIQUEIDENTIFIER = 'AB120005-AB00-0000-0000-000000000005'
-- Philippines
DECLARE @ChPH_Email     UNIQUEIDENTIFIER = 'AB130001-AB00-0000-0000-000000000001'
DECLARE @ChPH_Facebook  UNIQUEIDENTIFIER = 'AB130004-AB00-0000-0000-000000000004'
DECLARE @ChPH_Viber     UNIQUEIDENTIFIER = 'AB130003-AB00-0000-0000-000000000003'
-- UAE
DECLARE @ChUAE_WhatsApp UNIQUEIDENTIFIER = 'AB140001-AB00-0000-0000-000000000001'
DECLARE @ChUAE_LinkedIn UNIQUEIDENTIFIER = 'AB140004-AB00-0000-0000-000000000004'
DECLARE @ChUAE_Email    UNIQUEIDENTIFIER = 'AB140002-AB00-0000-0000-000000000002'

-- ── LeadSources (real existing) ───────────────────────────────────────
-- India
DECLARE @SrcIN_Website   UNIQUEIDENTIFIER = 'FF110001-FF00-0000-0000-000000000001'
DECLARE @SrcIN_Referral  UNIQUEIDENTIFIER = 'FF110002-FF00-0000-0000-000000000002'
DECLARE @SrcIN_LinkedIn  UNIQUEIDENTIFIER = 'FF110003-FF00-0000-0000-000000000003'
DECLARE @SrcIN_GoogleAds UNIQUEIDENTIFIER = 'FF110006-FF00-0000-0000-000000000006'
-- Thailand
DECLARE @SrcTH_Website   UNIQUEIDENTIFIER = 'FF120001-FF00-0000-0000-000000000001'
DECLARE @SrcTH_LINE      UNIQUEIDENTIFIER = 'FF120002-FF00-0000-0000-000000000002'
DECLARE @SrcTH_FBAds     UNIQUEIDENTIFIER = 'FF120003-FF00-0000-0000-000000000003'
DECLARE @SrcTH_Referral  UNIQUEIDENTIFIER = 'FF120004-FF00-0000-0000-000000000004'
-- Philippines
DECLARE @SrcPH_Website   UNIQUEIDENTIFIER = 'FF130001-FF00-0000-0000-000000000001'
DECLARE @SrcPH_Facebook  UNIQUEIDENTIFIER = 'FF130002-FF00-0000-0000-000000000002'
DECLARE @SrcPH_Referral  UNIQUEIDENTIFIER = 'FF130003-FF00-0000-0000-000000000003'
DECLARE @SrcPH_ColdCall  UNIQUEIDENTIFIER = 'FF130006-FF00-0000-0000-000000000006'
-- UAE
DECLARE @SrcUAE_LinkedIn UNIQUEIDENTIFIER = 'FF140002-FF00-0000-0000-000000000002'
DECLARE @SrcUAE_Referral UNIQUEIDENTIFIER = 'FF140003-FF00-0000-0000-000000000003'
DECLARE @SrcUAE_GITEX    UNIQUEIDENTIFIER = 'FF140004-FF00-0000-0000-000000000004'
DECLARE @SrcUAE_Partner  UNIQUEIDENTIFIER = 'FF140007-FF00-0000-0000-000000000007'

-- ── Lead IDs (fixed so script is re-runnable) ────────────────────────
-- India
DECLARE @LIN1 UNIQUEIDENTIFIER = 'FEED0001-0001-0000-0000-000000000001'
DECLARE @LIN2 UNIQUEIDENTIFIER = 'FEED0001-0002-0000-0000-000000000002'
DECLARE @LIN3 UNIQUEIDENTIFIER = 'FEED0001-0003-0000-0000-000000000003'
-- Thailand
DECLARE @LTH1 UNIQUEIDENTIFIER = 'FEED0002-0001-0000-0000-000000000001'
DECLARE @LTH2 UNIQUEIDENTIFIER = 'FEED0002-0002-0000-0000-000000000002'
DECLARE @LTH3 UNIQUEIDENTIFIER = 'FEED0002-0003-0000-0000-000000000003'
-- Philippines
DECLARE @LPH1 UNIQUEIDENTIFIER = 'FEED0003-0001-0000-0000-000000000001'
DECLARE @LPH2 UNIQUEIDENTIFIER = 'FEED0003-0002-0000-0000-000000000002'
DECLARE @LPH3 UNIQUEIDENTIFIER = 'FEED0003-0003-0000-0000-000000000003'
-- UAE
DECLARE @LUAE1 UNIQUEIDENTIFIER = 'FEED0004-0001-0000-0000-000000000001'
DECLARE @LUAE2 UNIQUEIDENTIFIER = 'FEED0004-0002-0000-0000-000000000002'
DECLARE @LUAE3 UNIQUEIDENTIFIER = 'FEED0004-0003-0000-0000-000000000003'

-- =====================================================================
-- INDIA LEADS  (INR · Rajesh Sharma)
-- Lead 1: Full profile · Qualified · IT Vertical   → expect ~80
-- Lead 2: Mid profile  · Working   · Banking        → expect ~50
-- Lead 3: Minimal      · New       · no vertical    → expect ~20
-- =====================================================================

IF NOT EXISTS (SELECT 1 FROM Leads WHERE Id=@LIN1)
    INSERT INTO Leads(Id,TenantId,FullName,Email,Phone,CompanyName,
        Address,CountryId,Currency,ChannelId,SourceId,VerticalId,
        Status,Score,EstimatedValue,OwnerUserId,Channel,Source,
        IsConverted,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(@LIN1,@TIN,
        'Priya Sharma','priya.sharma@nexatech.in','+91-98765-11001',
        'NexaTech Solutions Pvt Ltd',
        '14th Floor, Prestige Tower, MG Road, Bangalore 560001',
        @CIN,'INR',@ChIN_LinkedIn,@SrcIN_LinkedIn,@VIT,
        2,0,850000.00,@UIN,0,'LinkedIn',
        0,0,DATEADD(DAY,-18,GETUTCDATE()),'seed');

IF NOT EXISTS (SELECT 1 FROM Leads WHERE Id=@LIN2)
    INSERT INTO Leads(Id,TenantId,FullName,Email,Phone,CompanyName,
        CountryId,Currency,ChannelId,SourceId,VerticalId,
        Status,Score,EstimatedValue,OwnerUserId,Channel,Source,
        IsConverted,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(@LIN2,@TIN,
        'Arjun Mehta','arjun.mehta@hdbankcorp.in','+91-87654-22002',
        'HD Bank Corp',
        @CIN,'INR',@ChIN_Email,@SrcIN_Referral,@VBanking,
        1,0,320000.00,@UIN,0,'Referral',
        0,0,DATEADD(DAY,-9,GETUTCDATE()),'seed');

IF NOT EXISTS (SELECT 1 FROM Leads WHERE Id=@LIN3)
    INSERT INTO Leads(Id,TenantId,FullName,Email,Phone,
        CountryId,Currency,ChannelId,SourceId,
        Status,Score,OwnerUserId,Channel,Source,
        IsConverted,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(@LIN3,@TIN,
        'Sunita Patel','sunita.patel@gmail.com','+91-77543-33003',
        @CIN,'INR',@ChIN_WhatsApp,@SrcIN_GoogleAds,
        0,0,@UIN,0,'Google Ads',
        0,0,DATEADD(DAY,-3,GETUTCDATE()),'seed');

PRINT '✅ India Leads';

-- =====================================================================
-- THAILAND LEADS  (THB · Somchai Wiriya)
-- Lead 1: Full profile · Qualified · Logistics      → expect ~80
-- Lead 2: Mid profile  · Working   · Manufacturing  → expect ~50
-- Lead 3: Minimal      · New       · no vertical    → expect ~20
-- =====================================================================

IF NOT EXISTS (SELECT 1 FROM Leads WHERE Id=@LTH1)
    INSERT INTO Leads(Id,TenantId,FullName,Email,Phone,CompanyName,
        Address,CountryId,Currency,ChannelId,SourceId,VerticalId,
        Status,Score,EstimatedValue,OwnerUserId,Channel,Source,
        IsConverted,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(@LTH1,@TTH,
        'Somying Charoenwong','somying@thaicargo.co.th','+66-81-234-5678',
        'Thai Cargo Express Co. Ltd',
        '88 Rama IV Rd, Klong Toei, Bangkok 10110',
        @CTH,'THB',@ChTH_LINE,@SrcTH_LINE,@VLogistic,
        2,0,380000.00,@UTH,0,'LINE Official',
        0,0,DATEADD(DAY,-14,GETUTCDATE()),'seed');

IF NOT EXISTS (SELECT 1 FROM Leads WHERE Id=@LTH2)
    INSERT INTO Leads(Id,TenantId,FullName,Email,Phone,CompanyName,
        CountryId,Currency,ChannelId,SourceId,VerticalId,
        Status,Score,EstimatedValue,OwnerUserId,Channel,Source,
        IsConverted,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(@LTH2,@TTH,
        'Krit Phongphan','krit.p@manufactory.th','+66-89-876-5432',
        'Phongphan Manufacturing Ltd',
        @CTH,'THB',@ChTH_Facebook,@SrcTH_FBAds,@VManuf,
        1,0,95000.00,@UTH,0,'Facebook Ads',
        0,0,DATEADD(DAY,-6,GETUTCDATE()),'seed');

IF NOT EXISTS (SELECT 1 FROM Leads WHERE Id=@LTH3)
    INSERT INTO Leads(Id,TenantId,FullName,Phone,
        CountryId,Currency,ChannelId,SourceId,
        Status,Score,OwnerUserId,Channel,Source,
        IsConverted,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(@LTH3,@TTH,
        'Nattaporn Srisuk','+66-92-111-9999',
        @CTH,'THB',@ChTH_InPerson,@SrcTH_Referral,
        0,0,@UTH,0,'Referral',
        0,0,DATEADD(DAY,-1,GETUTCDATE()),'seed');

PRINT '✅ Thailand Leads';

-- =====================================================================
-- PHILIPPINES LEADS  (PHP · Maria Santos)
-- Lead 1: Full profile · Qualified · Banking        → expect ~80
-- Lead 2: Mid profile  · Working   · IT Vertical    → expect ~50
-- Lead 3: Minimal      · New       · no vertical    → expect ~20
-- =====================================================================

IF NOT EXISTS (SELECT 1 FROM Leads WHERE Id=@LPH1)
    INSERT INTO Leads(Id,TenantId,FullName,Email,Phone,CompanyName,
        Address,CountryId,Currency,ChannelId,SourceId,VerticalId,
        Status,Score,EstimatedValue,OwnerUserId,Channel,Source,
        IsConverted,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(@LPH1,@TPH,
        'Jose Reyes','jose.reyes@bpifintech.ph','+63-917-111-2001',
        'BPI FinTech Solutions Inc',
        'Unit 12B BGC One Tower, 32nd St, Taguig City 1634',
        @CPH,'PHP',@ChPH_Email,@SrcPH_Referral,@VBanking,
        2,0,1250000.00,@UPH,0,'Referral',
        0,0,DATEADD(DAY,-21,GETUTCDATE()),'seed');

IF NOT EXISTS (SELECT 1 FROM Leads WHERE Id=@LPH2)
    INSERT INTO Leads(Id,TenantId,FullName,Email,Phone,CompanyName,
        CountryId,Currency,ChannelId,SourceId,VerticalId,
        Status,Score,EstimatedValue,OwnerUserId,Channel,Source,
        IsConverted,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(@LPH2,@TPH,
        'Maria Clara Dizon','mclara@dizontech.com','+63-918-333-4002',
        'Dizon Tech Consulting',
        @CPH,'PHP',@ChPH_Facebook,@SrcPH_Facebook,@VIT,
        1,0,480000.00,@UPH,0,'Facebook',
        0,0,DATEADD(DAY,-8,GETUTCDATE()),'seed');

IF NOT EXISTS (SELECT 1 FROM Leads WHERE Id=@LPH3)
    INSERT INTO Leads(Id,TenantId,FullName,Phone,
        CountryId,Currency,ChannelId,SourceId,
        Status,Score,OwnerUserId,Channel,Source,
        IsConverted,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(@LPH3,@TPH,
        'Rodrigo Santos','+63-920-555-6003',
        @CPH,'PHP',@ChPH_Viber,@SrcPH_ColdCall,
        0,0,@UPH,0,'Cold Call',
        0,0,DATEADD(DAY,-2,GETUTCDATE()),'seed');

PRINT '✅ Philippines Leads';

-- =====================================================================
-- UAE LEADS  (AED · Ahmed Al-Rashidi)
-- Lead 1: Full profile · Qualified · Engineering    → expect ~80
-- Lead 2: Mid profile  · Working   · Logistics      → expect ~50
-- Lead 3: Minimal      · New       · no vertical    → expect ~20
-- =====================================================================

IF NOT EXISTS (SELECT 1 FROM Leads WHERE Id=@LUAE1)
    INSERT INTO Leads(Id,TenantId,FullName,Email,Phone,CompanyName,
        Address,CountryId,Currency,ChannelId,SourceId,VerticalId,
        Status,Score,EstimatedValue,OwnerUserId,Channel,Source,
        IsConverted,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(@LUAE1,@TUAE,
        'Mohammed Al Farsi','m.alfarsi@alphaeng.ae','+971-50-111-3001',
        'Alpha Engineering & Construction LLC',
        'Office 704, Maze Tower, Sheikh Zayed Rd, Dubai',
        @CUAE,'AED',@ChUAE_LinkedIn,@SrcUAE_GITEX,@VEngr,
        2,0,295000.00,@UUAE,0,'Exhibition / GITEX',
        0,0,DATEADD(DAY,-19,GETUTCDATE()),'seed');

IF NOT EXISTS (SELECT 1 FROM Leads WHERE Id=@LUAE2)
    INSERT INTO Leads(Id,TenantId,FullName,Email,Phone,CompanyName,
        CountryId,Currency,ChannelId,SourceId,VerticalId,
        Status,Score,EstimatedValue,OwnerUserId,Channel,Source,
        IsConverted,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(@LUAE2,@TUAE,
        'Fatima Al Hassan','fatima@gulftransport.ae','+971-55-333-4002',
        'Gulf Transport & Logistics Co',
        @CUAE,'AED',@ChUAE_WhatsApp,@SrcUAE_Partner,@VLogistic,
        1,0,112000.00,@UUAE,0,'Partner / Reseller',
        0,0,DATEADD(DAY,-7,GETUTCDATE()),'seed');

IF NOT EXISTS (SELECT 1 FROM Leads WHERE Id=@LUAE3)
    INSERT INTO Leads(Id,TenantId,FullName,Email,
        CountryId,Currency,ChannelId,SourceId,
        Status,Score,OwnerUserId,Channel,Source,
        IsConverted,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(@LUAE3,@TUAE,
        'Khalid Mansoor','khalid.m@outlook.com',
        @CUAE,'AED',@ChUAE_Email,@SrcUAE_Referral,
        0,0,@UUAE,0,'Referral',
        0,0,GETUTCDATE(),'seed');

PRINT '✅ UAE Leads';

-- =====================================================================
-- LEAD NOTES
-- Full leads (L1): 2 notes each
-- Mid leads  (L2): 1 note each
-- Minimal    (L3): 0 notes
-- =====================================================================

-- India L1
IF NOT EXISTS (SELECT 1 FROM LeadNotes WHERE LeadId=@LIN1 AND IsDeleted=0)
BEGIN
    INSERT INTO LeadNotes(Id,LeadId,TenantId,Note,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LIN1,@TIN,
        'Discovery call with Priya — 45 min. She is evaluating 3 CRM vendors. Key requirements: multi-currency (INR/USD), REST API for Tally integration, and mobile app. Budget is approved at ₹8.5L. Decision expected in 2 weeks.',
        0,DATEADD(DAY,-16,GETUTCDATE()),'seed'),
          (NEWID(),@LIN1,@TIN,
        'Sent product demo recording and INR pricing PDF. Priya confirmed IT team will review by March 12. She specifically liked the Pipeline Kanban board. Following up after their internal review.',
        0,DATEADD(DAY,-10,GETUTCDATE()),'seed');
END

-- India L2
IF NOT EXISTS (SELECT 1 FROM LeadNotes WHERE LeadId=@LIN2 AND IsDeleted=0)
    INSERT INTO LeadNotes(Id,LeadId,TenantId,Note,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LIN2,@TIN,
        'Arjun came through referral from existing client Kapoor Enterprises. HD Bank Corp runs 12 branches across Maharashtra. Interested in lead and contact modules — not deals yet. Timeline unclear.',
        0,DATEADD(DAY,-7,GETUTCDATE()),'seed');

-- Thailand L1
IF NOT EXISTS (SELECT 1 FROM LeadNotes WHERE LeadId=@LTH1 AND IsDeleted=0)
BEGIN
    INSERT INTO LeadNotes(Id,LeadId,TenantId,Note,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LTH1,@TTH,
        'Somying manages logistics ops for 6 hubs across Thailand. Contacted via LINE. Key requirement: track freight sales pipeline and shipper contacts. Requested Thai language UI — noted for roadmap.',
        0,DATEADD(DAY,-12,GETUTCDATE()),'seed'),
          (NEWID(),@LTH1,@TTH,
        'Demo call done over Zoom. She was impressed by the Kanban board and bulk contact import. THB currency displayed correctly throughout. Sending proposal with THB pricing today.',
        0,DATEADD(DAY,-5,GETUTCDATE()),'seed');
END

-- Thailand L2
IF NOT EXISTS (SELECT 1 FROM LeadNotes WHERE LeadId=@LTH2 AND IsDeleted=0)
    INSERT INTO LeadNotes(Id,LeadId,TenantId,Note,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LTH2,@TTH,
        'Krit runs a mid-size factory in Chonburi. Reached via Facebook Ad. Looking to replace Excel for tracking B2B customer pipeline. Needs custom fields for product SKUs. Demo scheduled next week.',
        0,DATEADD(DAY,-4,GETUTCDATE()),'seed');

-- Philippines L1
IF NOT EXISTS (SELECT 1 FROM LeadNotes WHERE LeadId=@LPH1 AND IsDeleted=0)
BEGIN
    INSERT INTO LeadNotes(Id,LeadId,TenantId,Note,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LPH1,@TPH,
        'Jose is VP Engineering at BPI FinTech. Very technical — reviewed our REST API docs live during call. Asked about webhook support and multi-tenant data isolation. Strong buying signal. PHP 1.25M budget confirmed.',
        0,DATEADD(DAY,-19,GETUTCDATE()),'seed'),
          (NEWID(),@LPH1,@TPH,
        'Second call with Jose + CFO. Agreed on Professional plan for 15 users at PHP 1.25M annual. Contract with their legal team. PHP currency showing correctly in all quote screens.',
        0,DATEADD(DAY,-8,GETUTCDATE()),'seed');
END

-- Philippines L2
IF NOT EXISTS (SELECT 1 FROM LeadNotes WHERE LeadId=@LPH2 AND IsDeleted=0)
    INSERT INTO LeadNotes(Id,LeadId,TenantId,Note,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LPH2,@TPH,
        'Maria contacted via Facebook. Runs IT consultancy with 8 staff in Cebu. Currently using spreadsheets — clear upgrade path. Needs demo of the lead scoring and deal conversion features.',
        0,DATEADD(DAY,-6,GETUTCDATE()),'seed');

-- UAE L1
IF NOT EXISTS (SELECT 1 FROM LeadNotes WHERE LeadId=@LUAE1 AND IsDeleted=0)
BEGIN
    INSERT INTO LeadNotes(Id,LeadId,TenantId,Note,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LUAE1,@TUAE,
        'Met Mohammed at GITEX 2026 booth. 30-min demo on the spot. Alpha Engineering handles AED 50M+ projects annually and needs pipeline CRM for BD team of 12. Very interested in white-label option.',
        0,DATEADD(DAY,-17,GETUTCDATE()),'seed'),
          (NEWID(),@LUAE1,@TUAE,
        'Partner onboarding call with Mohammed and his IT head. Discussed AED-denominated pricing and white-label branding. Revenue share model proposed at 20%. Legal reviewing partner agreement now.',
        0,DATEADD(DAY,-6,GETUTCDATE()),'seed');
END

-- UAE L2
IF NOT EXISTS (SELECT 1 FROM LeadNotes WHERE LeadId=@LUAE2 AND IsDeleted=0)
    INSERT INTO LeadNotes(Id,LeadId,TenantId,Note,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LUAE2,@TUAE,
        'Fatima manages B2B sales for Gulf Transport. 200+ wholesale shipping clients tracked manually. Needs CRM with Arabic name support and AED invoicing. Sent detailed AED proposal — awaiting response.',
        0,DATEADD(DAY,-5,GETUTCDATE()),'seed');

PRINT '✅ LeadNotes';

-- =====================================================================
-- LEAD ACTIVITIES
-- Full leads (L1): 2 activities
-- Mid leads  (L2): 1 activity
-- Minimal    (L3): 0 activities
-- =====================================================================

-- India L1
IF NOT EXISTS (SELECT 1 FROM LeadActivities WHERE LeadId=@LIN1 AND IsDeleted=0)
BEGIN
    INSERT INTO LeadActivities(Id,LeadId,TenantId,ActivityType,Subject,Description,Duration,ActivityDate,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LIN1,@TIN,'Call','Discovery call – Priya Sharma / NexaTech',
        'Discussed current Zoho CRM pain points. Priya confirmed multi-currency and API are blockers. 45-min call, very engaged.',
        45,DATEADD(DAY,-16,GETUTCDATE()),0,DATEADD(DAY,-16,GETUTCDATE()),'seed'),
          (NEWID(),@LIN1,@TIN,'Demo','Live product demo – NexaTech Solutions',
        'Full demo: Leads → Deals pipeline. Showed INR currency formatting, lead scoring, Kanban board. Priya and her PM attended.',
        60,DATEADD(DAY,-11,GETUTCDATE()),0,DATEADD(DAY,-11,GETUTCDATE()),'seed');
END

-- India L2
IF NOT EXISTS (SELECT 1 FROM LeadActivities WHERE LeadId=@LIN2 AND IsDeleted=0)
    INSERT INTO LeadActivities(Id,LeadId,TenantId,ActivityType,Subject,Description,Duration,ActivityDate,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LIN2,@TIN,'Call','Intro call – Arjun Mehta / HD Bank Corp',
        'Short 20-min intro. Confirmed referral from Kapoor Enterprises. Interested in lead module only for now. Timeline Q2 2026.',
        20,DATEADD(DAY,-7,GETUTCDATE()),0,DATEADD(DAY,-7,GETUTCDATE()),'seed');

-- Thailand L1
IF NOT EXISTS (SELECT 1 FROM LeadActivities WHERE LeadId=@LTH1 AND IsDeleted=0)
BEGIN
    INSERT INTO LeadActivities(Id,LeadId,TenantId,ActivityType,Subject,Description,Duration,ActivityDate,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LTH1,@TTH,'Meeting','On-site visit – Thai Cargo Express',
        'Visited Klong Toei office. Met Somying and ops team. Reviewed manual freight sales tracking process — clear CRM need.',
        90,DATEADD(DAY,-13,GETUTCDATE()),0,DATEADD(DAY,-13,GETUTCDATE()),'seed'),
          (NEWID(),@LTH1,@TTH,'Demo','Zoom demo – Pipeline + Lead scoring',
        'Showed THB currency throughout all screens. Drag-and-drop Kanban loved by the team. Lead score calculation explained.',
        45,DATEADD(DAY,-6,GETUTCDATE()),0,DATEADD(DAY,-6,GETUTCDATE()),'seed');
END

-- Thailand L2
IF NOT EXISTS (SELECT 1 FROM LeadActivities WHERE LeadId=@LTH2 AND IsDeleted=0)
    INSERT INTO LeadActivities(Id,LeadId,TenantId,ActivityType,Subject,Description,Duration,ActivityDate,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LTH2,@TTH,'Call','LINE call – Krit Phongphan',
        'Quick 30-min LINE video call. Factory runs 4 production lines. Needs mobile-friendly CRM. Demo to be arranged this week.',
        30,DATEADD(DAY,-4,GETUTCDATE()),0,DATEADD(DAY,-4,GETUTCDATE()),'seed');

-- Philippines L1
IF NOT EXISTS (SELECT 1 FROM LeadActivities WHERE LeadId=@LPH1 AND IsDeleted=0)
BEGIN
    INSERT INTO LeadActivities(Id,LeadId,TenantId,ActivityType,Subject,Description,Duration,ActivityDate,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LPH1,@TPH,'Call','Technical deep-dive – Jose Reyes / BPI FinTech',
        'Jose reviewed REST API docs live. Asked about rate limits, webhook events, and GDPR compliance. Highly technical, very positive.',
        60,DATEADD(DAY,-18,GETUTCDATE()),0,DATEADD(DAY,-18,GETUTCDATE()),'seed'),
          (NEWID(),@LPH1,@TPH,'Meeting','Contract negotiation – BPI FinTech + CFO',
        'Zoom with Jose and CFO Ligaya Cruz. Agreed Professional plan PHP 1.25M/year for 15 users. Signing pending legal review.',
        45,DATEADD(DAY,-9,GETUTCDATE()),0,DATEADD(DAY,-9,GETUTCDATE()),'seed');
END

-- Philippines L2
IF NOT EXISTS (SELECT 1 FROM LeadActivities WHERE LeadId=@LPH2 AND IsDeleted=0)
    INSERT INTO LeadActivities(Id,LeadId,TenantId,ActivityType,Subject,Description,Duration,ActivityDate,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LPH2,@TPH,'Demo','Online demo – Dizon Tech Consulting',
        'Showed lead module and scoring dashboard. PHP currency formatted correctly. Maria asked about bulk contact import from Excel.',
        40,DATEADD(DAY,-6,GETUTCDATE()),0,DATEADD(DAY,-6,GETUTCDATE()),'seed');

-- UAE L1
IF NOT EXISTS (SELECT 1 FROM LeadActivities WHERE LeadId=@LUAE1 AND IsDeleted=0)
BEGIN
    INSERT INTO LeadActivities(Id,LeadId,TenantId,ActivityType,Subject,Description,Duration,ActivityDate,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LUAE1,@TUAE,'Meeting','GITEX 2026 – Mohammed Al Farsi',
        'In-person at our GITEX booth. 30-min live demo. Mohammed took brochure and pricing card. Expressed strong interest in white-label BD pipeline tool.',
        30,DATEADD(DAY,-18,GETUTCDATE()),0,DATEADD(DAY,-18,GETUTCDATE()),'seed'),
          (NEWID(),@LUAE1,@TUAE,'Call','Partner deal structure call',
        'Discussed AED white-label pricing. 20% revenue share on resold seats. Mohammed will revert after board review. AED values confirmed correct throughout.',
        60,DATEADD(DAY,-7,GETUTCDATE()),0,DATEADD(DAY,-7,GETUTCDATE()),'seed');
END

-- UAE L2
IF NOT EXISTS (SELECT 1 FROM LeadActivities WHERE LeadId=@LUAE2 AND IsDeleted=0)
    INSERT INTO LeadActivities(Id,LeadId,TenantId,ActivityType,Subject,Description,Duration,ActivityDate,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LUAE2,@TUAE,'Email','AED proposal sent – Gulf Transport',
        'Sent detailed 3-page proposal with AED 112,000 annual pricing and ROI breakdown. Awaiting Fatima response.',
        0,DATEADD(DAY,-5,GETUTCDATE()),0,DATEADD(DAY,-5,GETUTCDATE()),'seed');

PRINT '✅ LeadActivities';

-- =====================================================================
-- LEAD REMINDERS
-- Full leads (L1): 2 reminders (1 pending upcoming, 1 completed past)
-- Mid leads  (L2): 1 reminder (pending)
-- Minimal    (L3): 0 reminders
-- =====================================================================

-- India L1
IF NOT EXISTS (SELECT 1 FROM LeadReminders WHERE LeadId=@LIN1 AND IsDeleted=0)
BEGIN
    INSERT INTO LeadReminders(Id,LeadId,TenantId,Title,Description,ReminderDate,IsCompleted,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LIN1,@TIN,
        'Follow up – IT team decision',
        'Priya said IT review completes around this date. Call to confirm sign-off and next steps.',
        DATEADD(DAY,3,GETUTCDATE()),0,0,DATEADD(DAY,-10,GETUTCDATE()),'seed');
    INSERT INTO LeadReminders(Id,LeadId,TenantId,Title,Description,ReminderDate,IsCompleted,CompletedAtUtc,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LIN1,@TIN,
        'Send INR pricing PDF',
        'Completed – pricing PDF (INR) sent to priya.sharma@nexatech.in.',
        DATEADD(DAY,-10,GETUTCDATE()),1,DATEADD(DAY,-10,GETUTCDATE()),0,DATEADD(DAY,-16,GETUTCDATE()),'seed');
END

-- India L2
IF NOT EXISTS (SELECT 1 FROM LeadReminders WHERE LeadId=@LIN2 AND IsDeleted=0)
    INSERT INTO LeadReminders(Id,LeadId,TenantId,Title,Description,ReminderDate,IsCompleted,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LIN2,@TIN,
        'Schedule product demo',
        'Arjun asked for a full demo this week. Confirm time via email.',
        DATEADD(DAY,4,GETUTCDATE()),0,0,DATEADD(DAY,-7,GETUTCDATE()),'seed');

-- Thailand L1
IF NOT EXISTS (SELECT 1 FROM LeadReminders WHERE LeadId=@LTH1 AND IsDeleted=0)
BEGIN
    INSERT INTO LeadReminders(Id,LeadId,TenantId,Title,Description,ReminderDate,IsCompleted,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LTH1,@TTH,
        'Send THB proposal doc',
        'Somying requested proposal in Thai Baht with local tax breakdown. Finalize and send today.',
        DATEADD(DAY,1,GETUTCDATE()),0,0,DATEADD(DAY,-6,GETUTCDATE()),'seed');
    INSERT INTO LeadReminders(Id,LeadId,TenantId,Title,Description,ReminderDate,IsCompleted,CompletedAtUtc,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LTH1,@TTH,
        'Confirm demo recording sent via LINE',
        'Completed – screen-recorded demo sent to Somying on LINE.',
        DATEADD(DAY,-12,GETUTCDATE()),1,DATEADD(DAY,-12,GETUTCDATE()),0,DATEADD(DAY,-13,GETUTCDATE()),'seed');
END

-- Thailand L2
IF NOT EXISTS (SELECT 1 FROM LeadReminders WHERE LeadId=@LTH2 AND IsDeleted=0)
    INSERT INTO LeadReminders(Id,LeadId,TenantId,Title,Description,ReminderDate,IsCompleted,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LTH2,@TTH,
        'Arrange demo for Krit',
        'Krit confirmed availability this week. Book Zoom slot and send invite via LINE.',
        DATEADD(DAY,2,GETUTCDATE()),0,0,DATEADD(DAY,-4,GETUTCDATE()),'seed');

-- Philippines L1
IF NOT EXISTS (SELECT 1 FROM LeadReminders WHERE LeadId=@LPH1 AND IsDeleted=0)
BEGIN
    INSERT INTO LeadReminders(Id,LeadId,TenantId,Title,Description,ReminderDate,IsCompleted,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LPH1,@TPH,
        'Contract signing follow-up',
        'Jose said legal review done by this date. Call to confirm signing and kick-off schedule.',
        DATEADD(DAY,5,GETUTCDATE()),0,0,DATEADD(DAY,-9,GETUTCDATE()),'seed');
    INSERT INTO LeadReminders(Id,LeadId,TenantId,Title,Description,ReminderDate,IsCompleted,CompletedAtUtc,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LPH1,@TPH,
        'Send REST API documentation',
        'Completed – shared API docs link and Postman collection with Jose.',
        DATEADD(DAY,-17,GETUTCDATE()),1,DATEADD(DAY,-17,GETUTCDATE()),0,DATEADD(DAY,-19,GETUTCDATE()),'seed');
END

-- Philippines L2
IF NOT EXISTS (SELECT 1 FROM LeadReminders WHERE LeadId=@LPH2 AND IsDeleted=0)
    INSERT INTO LeadReminders(Id,LeadId,TenantId,Title,Description,ReminderDate,IsCompleted,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LPH2,@TPH,
        'Check decision after partner discussion',
        'Maria said she needs to discuss with business partner before committing. Follow up by this date.',
        DATEADD(DAY,3,GETUTCDATE()),0,0,DATEADD(DAY,-6,GETUTCDATE()),'seed');

-- UAE L1
IF NOT EXISTS (SELECT 1 FROM LeadReminders WHERE LeadId=@LUAE1 AND IsDeleted=0)
BEGIN
    INSERT INTO LeadReminders(Id,LeadId,TenantId,Title,Description,ReminderDate,IsCompleted,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LUAE1,@TUAE,
        'Partner agreement – board decision deadline',
        'Mohammed said board review completes around this date. Call to get final sign-off on partner deal.',
        DATEADD(DAY,2,GETUTCDATE()),0,0,DATEADD(DAY,-7,GETUTCDATE()),'seed');
    INSERT INTO LeadReminders(Id,LeadId,TenantId,Title,Description,ReminderDate,IsCompleted,CompletedAtUtc,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LUAE1,@TUAE,
        'Send GITEX follow-up email with AED pricing',
        'Completed – sent post-GITEX email with AED proposal and white-label brochure.',
        DATEADD(DAY,-17,GETUTCDATE()),1,DATEADD(DAY,-17,GETUTCDATE()),0,DATEADD(DAY,-18,GETUTCDATE()),'seed');
END

-- UAE L2
IF NOT EXISTS (SELECT 1 FROM LeadReminders WHERE LeadId=@LUAE2 AND IsDeleted=0)
    INSERT INTO LeadReminders(Id,LeadId,TenantId,Title,Description,ReminderDate,IsCompleted,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@LUAE2,@TUAE,
        'Follow up on AED proposal – Fatima',
        'Proposal sent 5 days ago with no response. Call Fatima to confirm receipt and address questions.',
        DATEADD(DAY,1,GETUTCDATE()),0,0,DATEADD(DAY,-5,GETUTCDATE()),'seed');

PRINT '✅ LeadReminders';

-- =====================================================================
-- VERIFICATION QUERY
-- Expected score = profile fields + notes×5(max 15) + activities×5(max 20) + status bonus
-- =====================================================================

SELECT
    T.Name                                          AS Tenant,
    L.Currency,
    L.FullName                                      AS Lead,
    CASE L.Status WHEN 0 THEN 'New' WHEN 1 THEN 'Working' WHEN 2 THEN 'Qualified' END AS Status,
    FORMAT(L.EstimatedValue, 'N0')                  AS EstimatedValue,
    (
        CASE WHEN L.Email       IS NOT NULL THEN 10 ELSE 0 END +
        CASE WHEN L.Phone       IS NOT NULL THEN 10 ELSE 0 END +
        CASE WHEN L.CompanyName IS NOT NULL THEN 10 ELSE 0 END +
        CASE WHEN L.CountryId   IS NOT NULL THEN 5  ELSE 0 END +
        CASE WHEN L.VerticalId  IS NOT NULL THEN 5  ELSE 0 END +
        CASE WHEN ISNULL(L.EstimatedValue,0) > 0    THEN 5 ELSE 0 END +
        CASE WHEN L.ChannelId   IS NOT NULL THEN 3  ELSE 0 END +
        CASE WHEN L.SourceId    IS NOT NULL THEN 2  ELSE 0 END +
        -- Notes max 15
        CASE
            WHEN (SELECT COUNT(*) FROM LeadNotes N WHERE N.LeadId=L.Id AND N.IsDeleted=0) >= 3 THEN 15
            WHEN (SELECT COUNT(*) FROM LeadNotes N WHERE N.LeadId=L.Id AND N.IsDeleted=0) = 2  THEN 10
            WHEN (SELECT COUNT(*) FROM LeadNotes N WHERE N.LeadId=L.Id AND N.IsDeleted=0) = 1  THEN 5
            ELSE 0
        END +
        -- Activities max 20
        CASE
            WHEN (SELECT COUNT(*) FROM LeadActivities A WHERE A.LeadId=L.Id AND A.IsDeleted=0) >= 4 THEN 20
            WHEN (SELECT COUNT(*) FROM LeadActivities A WHERE A.LeadId=L.Id AND A.IsDeleted=0) = 3  THEN 15
            WHEN (SELECT COUNT(*) FROM LeadActivities A WHERE A.LeadId=L.Id AND A.IsDeleted=0) = 2  THEN 10
            WHEN (SELECT COUNT(*) FROM LeadActivities A WHERE A.LeadId=L.Id AND A.IsDeleted=0) = 1  THEN 5
            ELSE 0
        END +
        -- Status bonus
        CASE L.Status WHEN 1 THEN 5 WHEN 2 THEN 15 ELSE 0 END
    )                                               AS ExpectedScore,
    (SELECT COUNT(*) FROM LeadNotes      N WHERE N.LeadId=L.Id AND N.IsDeleted=0)                     AS Notes,
    (SELECT COUNT(*) FROM LeadActivities A WHERE A.LeadId=L.Id AND A.IsDeleted=0)                     AS Activities,
    (SELECT COUNT(*) FROM LeadReminders  R WHERE R.LeadId=L.Id AND R.IsDeleted=0 AND R.IsCompleted=0) AS PendingReminders,
    (SELECT COUNT(*) FROM LeadReminders  R WHERE R.LeadId=L.Id AND R.IsDeleted=0 AND R.IsCompleted=1) AS CompletedReminders
FROM Leads L
JOIN Tenants T ON T.Id = L.TenantId
WHERE L.IsDeleted = 0
  AND L.CreatedBy = 'seed'
ORDER BY T.Name, ExpectedScore DESC;

COMMIT;
PRINT '';
PRINT '✅ Done. Open each lead in the UI to trigger RecalculateAsync and update the Score column.';
