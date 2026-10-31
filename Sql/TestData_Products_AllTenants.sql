-- =====================================================================
-- TestData_Products_AllTenants.sql
-- Products test data for India (INR), Thailand (THB),
-- Philippines (PHP), UAE (AED)
--
-- Note: No Currency column on Products table — currency is derived
-- from tenant via ICurrentTenantService at runtime.
--
-- 6 products per tenant × 4 tenants = 24 products total
-- Mix of: Product / Service / Subscription types
-- Mix of: Active / Inactive
-- Mix of: With tax rate / Without
-- =====================================================================

SET NOCOUNT ON;
BEGIN TRANSACTION;

-- ── Tenant IDs ────────────────────────────────────────────────────────
DECLARE @TIN  UNIQUEIDENTIFIER = 'BB100001-AAAA-0000-0000-000000000001'  -- India  INR
DECLARE @TTH  UNIQUEIDENTIFIER = 'BB100002-AAAA-0000-0000-000000000002'  -- Thailand THB
DECLARE @TPH  UNIQUEIDENTIFIER = 'BB100003-AAAA-0000-0000-000000000003'  -- Philippines PHP
DECLARE @TUAE UNIQUEIDENTIFIER = 'BB100004-AAAA-0000-0000-000000000004'  -- UAE AED

-- =====================================================================
-- INDIA PRODUCTS  (INR · GST 18% standard · 12% reduced · 0% exempt)
-- Realistic SaaS / IT services pricing in Indian market
-- =====================================================================

IF NOT EXISTS (SELECT 1 FROM Products WHERE TenantId=@TIN AND Sku='IN-SAAS-PRO')
    INSERT INTO Products(Id,TenantId,Name,Description,Sku,Category,Type,ListPrice,TaxRate,IsActive,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@TIN,
        'CRM Professional Plan',
        'Full CRM suite — Leads, Deals, Contacts, Pipeline. Up to 10 users. Monthly billing.',
        'IN-SAAS-PRO','Software','Subscription',
        12999.00, 18.00, 1, 0, DATEADD(DAY,-30,GETUTCDATE()),'seed');

IF NOT EXISTS (SELECT 1 FROM Products WHERE TenantId=@TIN AND Sku='IN-SAAS-ENT')
    INSERT INTO Products(Id,TenantId,Name,Description,Sku,Category,Type,ListPrice,TaxRate,IsActive,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@TIN,
        'CRM Enterprise Plan',
        'Unlimited users, white-label, API access, dedicated support, SLA 99.9%.',
        'IN-SAAS-ENT','Software','Subscription',
        49999.00, 18.00, 1, 0, DATEADD(DAY,-30,GETUTCDATE()),'seed');

IF NOT EXISTS (SELECT 1 FROM Products WHERE TenantId=@TIN AND Sku='IN-IMPL-STD')
    INSERT INTO Products(Id,TenantId,Name,Description,Sku,Category,Type,ListPrice,TaxRate,IsActive,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@TIN,
        'Implementation & Onboarding',
        'Standard onboarding: data migration, 2 training sessions, go-live support. One-time.',
        'IN-IMPL-STD','Services','Service',
        25000.00, 18.00, 1, 0, DATEADD(DAY,-25,GETUTCDATE()),'seed');

IF NOT EXISTS (SELECT 1 FROM Products WHERE TenantId=@TIN AND Sku='IN-SUPP-ANN')
    INSERT INTO Products(Id,TenantId,Name,Description,Sku,Category,Type,ListPrice,TaxRate,IsActive,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@TIN,
        'Annual Support Contract',
        'Priority email + phone support, 4hr SLA, quarterly health checks. 12 months.',
        'IN-SUPP-ANN','Services','Service',
        18000.00, 18.00, 1, 0, DATEADD(DAY,-20,GETUTCDATE()),'seed');

IF NOT EXISTS (SELECT 1 FROM Products WHERE TenantId=@TIN AND Sku='IN-TRAIN-GRP')
    INSERT INTO Products(Id,TenantId,Name,Description,Sku,Category,Type,ListPrice,TaxRate,IsActive,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@TIN,
        'Group Training Session',
        'On-site or virtual training for up to 15 users. Half-day session, includes materials.',
        'IN-TRAIN-GRP','Services','Service',
        8500.00, 18.00, 1, 0, DATEADD(DAY,-15,GETUTCDATE()),'seed');

IF NOT EXISTS (SELECT 1 FROM Products WHERE TenantId=@TIN AND Sku='IN-SAAS-STR')
    INSERT INTO Products(Id,TenantId,Name,Description,Sku,Category,Type,ListPrice,TaxRate,IsActive,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@TIN,
        'CRM Starter Plan',
        'Basic CRM for small teams — up to 3 users, leads and contacts only. DISCONTINUED.',
        'IN-SAAS-STR','Software','Subscription',
        2999.00, 18.00, 0, 0, DATEADD(DAY,-60,GETUTCDATE()),'seed');  -- Inactive

PRINT '✅ India Products (6)';

-- =====================================================================
-- THAILAND PRODUCTS  (THB · VAT 7%)
-- Realistic pricing in Thai baht for SME market
-- =====================================================================

IF NOT EXISTS (SELECT 1 FROM Products WHERE TenantId=@TTH AND Sku='TH-CRM-PRO')
    INSERT INTO Products(Id,TenantId,Name,Description,Sku,Category,Type,ListPrice,TaxRate,IsActive,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@TTH,
        'แพ็กเกจ CRM Pro (CRM Pro Package)',
        'ระบบ CRM ครบวงจร สำหรับทีมสูงสุด 10 คน รวม Leads, Deals และ Pipeline',
        'TH-CRM-PRO','Software','Subscription',
        4500.00, 7.00, 1, 0, DATEADD(DAY,-30,GETUTCDATE()),'seed');

IF NOT EXISTS (SELECT 1 FROM Products WHERE TenantId=@TTH AND Sku='TH-CRM-ENT')
    INSERT INTO Products(Id,TenantId,Name,Description,Sku,Category,Type,ListPrice,TaxRate,IsActive,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@TTH,
        'แพ็กเกจ CRM Enterprise',
        'ผู้ใช้ไม่จำกัด API เข้าถึงได้ ซัพพอร์ตเฉพาะ SLA 99.9%',
        'TH-CRM-ENT','Software','Subscription',
        15900.00, 7.00, 1, 0, DATEADD(DAY,-30,GETUTCDATE()),'seed');

IF NOT EXISTS (SELECT 1 FROM Products WHERE TenantId=@TTH AND Sku='TH-IMPL-01')
    INSERT INTO Products(Id,TenantId,Name,Description,Sku,Category,Type,ListPrice,TaxRate,IsActive,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@TTH,
        'บริการติดตั้งและอบรม (Setup & Training)',
        'ติดตั้งระบบ นำเข้าข้อมูล อบรม 2 ครั้ง รองรับ go-live ครั้งเดียว',
        'TH-IMPL-01','Services','Service',
        8900.00, 7.00, 1, 0, DATEADD(DAY,-25,GETUTCDATE()),'seed');

IF NOT EXISTS (SELECT 1 FROM Products WHERE TenantId=@TTH AND Sku='TH-SUPP-12M')
    INSERT INTO Products(Id,TenantId,Name,Description,Sku,Category,Type,ListPrice,TaxRate,IsActive,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@TTH,
        'สัญญาซัพพอร์ตรายปี (Annual Support)',
        'ซัพพอร์ตทางโทรศัพท์และอีเมล SLA 4 ชั่วโมง ตรวจสอบสุขภาพระบบทุกไตรมาส',
        'TH-SUPP-12M','Services','Service',
        5900.00, 7.00, 1, 0, DATEADD(DAY,-20,GETUTCDATE()),'seed');

IF NOT EXISTS (SELECT 1 FROM Products WHERE TenantId=@TTH AND Sku='TH-API-ADD')
    INSERT INTO Products(Id,TenantId,Name,Description,Sku,Category,Type,ListPrice,TaxRate,IsActive,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@TTH,
        'API Integration Add-on',
        'Custom API integration with LINE OA, Lazada, or Shopee. One-time setup fee.',
        'TH-API-ADD','Software','Product',
        12000.00, 7.00, 1, 0, DATEADD(DAY,-10,GETUTCDATE()),'seed');

IF NOT EXISTS (SELECT 1 FROM Products WHERE TenantId=@TTH AND Sku='TH-CRM-FREE')
    INSERT INTO Products(Id,TenantId,Name,Description,Sku,Category,Type,ListPrice,TaxRate,IsActive,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@TTH,
        'CRM Free Trial (30 วัน)',
        'ทดลองใช้ฟรี 30 วัน สำหรับทีมสูงสุด 3 คน ไม่ต้องใช้บัตรเครดิต',
        'TH-CRM-FREE','Software','Subscription',
        0.00, 0.00, 0, 0, DATEADD(DAY,-60,GETUTCDATE()),'seed');  -- Inactive (retired promo)

PRINT '✅ Thailand Products (6)';

-- =====================================================================
-- PHILIPPINES PRODUCTS  (PHP · VAT 12%)
-- Realistic pricing in Philippine peso
-- =====================================================================

IF NOT EXISTS (SELECT 1 FROM Products WHERE TenantId=@TPH AND Sku='PH-CRM-PRO')
    INSERT INTO Products(Id,TenantId,Name,Description,Sku,Category,Type,ListPrice,TaxRate,IsActive,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@TPH,
        'CRM Professional Plan',
        'Complete CRM for growing teams — up to 10 users. Leads, Deals, Contacts, Pipeline included.',
        'PH-CRM-PRO','Software','Subscription',
        5500.00, 12.00, 1, 0, DATEADD(DAY,-30,GETUTCDATE()),'seed');

IF NOT EXISTS (SELECT 1 FROM Products WHERE TenantId=@TPH AND Sku='PH-CRM-ENT')
    INSERT INTO Products(Id,TenantId,Name,Description,Sku,Category,Type,ListPrice,TaxRate,IsActive,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@TPH,
        'CRM Enterprise Plan',
        'Unlimited users, white-label option, REST API, dedicated account manager, 99.9% SLA.',
        'PH-CRM-ENT','Software','Subscription',
        19500.00, 12.00, 1, 0, DATEADD(DAY,-30,GETUTCDATE()),'seed');

IF NOT EXISTS (SELECT 1 FROM Products WHERE TenantId=@TPH AND Sku='PH-IMPL-STD')
    INSERT INTO Products(Id,TenantId,Name,Description,Sku,Category,Type,ListPrice,TaxRate,IsActive,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@TPH,
        'Standard Implementation',
        'System setup, data migration from Excel/CSV, 2 training sessions, go-live support. One-time fee.',
        'PH-IMPL-STD','Services','Service',
        12000.00, 12.00, 1, 0, DATEADD(DAY,-25,GETUTCDATE()),'seed');

IF NOT EXISTS (SELECT 1 FROM Products WHERE TenantId=@TPH AND Sku='PH-SUPP-ANN')
    INSERT INTO Products(Id,TenantId,Name,Description,Sku,Category,Type,ListPrice,TaxRate,IsActive,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@TPH,
        'Annual Maintenance & Support',
        'Priority support via Viber/email, 4hr response SLA, monthly system health report.',
        'PH-SUPP-ANN','Services','Service',
        8400.00, 12.00, 1, 0, DATEADD(DAY,-20,GETUTCDATE()),'seed');

IF NOT EXISTS (SELECT 1 FROM Products WHERE TenantId=@TPH AND Sku='PH-ADDUSER')
    INSERT INTO Products(Id,TenantId,Name,Description,Sku,Category,Type,ListPrice,TaxRate,IsActive,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@TPH,
        'Additional User License',
        'Add one extra user seat to any active plan. Billed monthly per seat.',
        'PH-ADDUSER','Software','Subscription',
        550.00, 12.00, 1, 0, DATEADD(DAY,-15,GETUTCDATE()),'seed');

IF NOT EXISTS (SELECT 1 FROM Products WHERE TenantId=@TPH AND Sku='PH-REPORT-ADD')
    INSERT INTO Products(Id,TenantId,Name,Description,Sku,Category,Type,ListPrice,TaxRate,IsActive,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@TPH,
        'Advanced Reports Add-on',
        'Custom dashboards, scheduled reports, Excel/PDF export. Add-on for any plan. COMING SOON.',
        'PH-REPORT-ADD','Software','Product',
        2200.00, 12.00, 0, 0, DATEADD(DAY,-10,GETUTCDATE()),'seed');  -- Inactive (not released yet)

PRINT '✅ Philippines Products (6)';

-- =====================================================================
-- UAE PRODUCTS  (AED · VAT 5%)
-- Realistic pricing in UAE Dirham for enterprise/SME market
-- =====================================================================

IF NOT EXISTS (SELECT 1 FROM Products WHERE TenantId=@TUAE AND Sku='AE-CRM-PRO')
    INSERT INTO Products(Id,TenantId,Name,Description,Sku,Category,Type,ListPrice,TaxRate,IsActive,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@TUAE,
        'CRM Professional Plan',
        'Full CRM platform — Leads, Deals, Contacts, Pipeline. Up to 10 users. Monthly billing.',
        'AE-CRM-PRO','Software','Subscription',
        1499.00, 5.00, 1, 0, DATEADD(DAY,-30,GETUTCDATE()),'seed');

IF NOT EXISTS (SELECT 1 FROM Products WHERE TenantId=@TUAE AND Sku='AE-CRM-ENT')
    INSERT INTO Products(Id,TenantId,Name,Description,Sku,Category,Type,ListPrice,TaxRate,IsActive,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@TUAE,
        'CRM Enterprise Plan',
        'Unlimited users, white-label branding, API access, dedicated support manager, 99.9% SLA.',
        'AE-CRM-ENT','Software','Subscription',
        4999.00, 5.00, 1, 0, DATEADD(DAY,-30,GETUTCDATE()),'seed');

IF NOT EXISTS (SELECT 1 FROM Products WHERE TenantId=@TUAE AND Sku='AE-IMPL-PREM')
    INSERT INTO Products(Id,TenantId,Name,Description,Sku,Category,Type,ListPrice,TaxRate,IsActive,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@TUAE,
        'Premium Implementation Package',
        'Full setup, data migration, custom fields configuration, 3 training sessions, on-site go-live support.',
        'AE-IMPL-PREM','Services','Service',
        7500.00, 5.00, 1, 0, DATEADD(DAY,-25,GETUTCDATE()),'seed');

IF NOT EXISTS (SELECT 1 FROM Products WHERE TenantId=@TUAE AND Sku='AE-SUPP-ANN')
    INSERT INTO Products(Id,TenantId,Name,Description,Sku,Category,Type,ListPrice,TaxRate,IsActive,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@TUAE,
        'Annual Support & Maintenance',
        'Dedicated WhatsApp support line, 2hr SLA, quarterly business reviews, free minor upgrades.',
        'AE-SUPP-ANN','Services','Service',
        3600.00, 5.00, 1, 0, DATEADD(DAY,-20,GETUTCDATE()),'seed');

IF NOT EXISTS (SELECT 1 FROM Products WHERE TenantId=@TUAE AND Sku='AE-WLABEL')
    INSERT INTO Products(Id,TenantId,Name,Description,Sku,Category,Type,ListPrice,TaxRate,IsActive,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@TUAE,
        'White-Label Reseller Package',
        'Full white-label license for partners. Custom domain, branding, up to 50 sub-tenants included.',
        'AE-WLABEL','Software','Product',
        18500.00, 5.00, 1, 0, DATEADD(DAY,-15,GETUTCDATE()),'seed');

IF NOT EXISTS (SELECT 1 FROM Products WHERE TenantId=@TUAE AND Sku='AE-CRM-STR')
    INSERT INTO Products(Id,TenantId,Name,Description,Sku,Category,Type,ListPrice,TaxRate,IsActive,IsDeleted,CreatedAtUtc,CreatedBy)
    VALUES(NEWID(),@TUAE,
        'CRM Starter Plan',
        'Entry-level plan for micro businesses — 3 users, contacts and leads only. RETIRED.',
        'AE-CRM-STR','Software','Subscription',
        299.00, 5.00, 0, 0, DATEADD(DAY,-90,GETUTCDATE()),'seed');  -- Inactive

PRINT '✅ UAE Products (6)';

COMMIT;

-- =====================================================================
-- VERIFY
-- =====================================================================

SELECT
    T.Name                                  AS Tenant,
    T.DefaultCurrency                       AS Currency,
    P.Category,
    P.Type,
    P.Name                                  AS Product,
    P.Sku,
    FORMAT(P.ListPrice, 'N2')               AS ListPrice,
    CAST(P.TaxRate AS NVARCHAR(10)) + '%'   AS TaxRate,
    CASE P.IsActive WHEN 1 THEN 'Active' ELSE 'Inactive' END AS Status
FROM Products P
JOIN Tenants T ON T.Id = P.TenantId
WHERE P.IsDeleted = 0
  AND P.CreatedBy = 'seed'
ORDER BY T.Name, P.Category, P.Type, P.ListPrice;

PRINT '';
PRINT '✅ Done — 24 products across 4 tenants.';
PRINT '   5 active + 1 inactive per tenant.';
PRINT '   Tax rates match each country: IN=18%, TH=7%, PH=12%, AE=5%';
