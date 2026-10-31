-- ══════════════════════════════════════════════════════════════════════
-- Demo tenant rebrand + role parity across markets
--
-- WHAT THIS DOES:
--   1. Renames the 3 non-Thailand tenants (+ Thailand itself, for
--      consistency) from generic "MadeeFlow <Country>" to realistic
--      fictitious company names, one per industry.
--   2. Updates each admin user's email to match the new domain.
--   3. Adds 3 new users per tenant (sales_manager / sales_rep / viewer)
--      for India, Philippines, and UAE — mirroring the pattern Thailand
--      already has (Nattaporn / Kritsana / Sutida). Reuses the SAME 4
--      global Role rows (Roles has no TenantId — confirmed from schema —
--      so no new Role rows are needed, only new Users + UserRoles).
--
-- SAFE TO RUN ONCE. Re-running will fail on the INSERTs (duplicate PKs)
-- rather than silently duplicating rows — that's intentional.
-- ══════════════════════════════════════════════════════════════════════

BEGIN TRANSACTION;

-- ══════════════════════════════════════════════════════════════════════
-- STEP 1: Rename tenants
-- ══════════════════════════════════════════════════════════════════════

-- ── Thailand → Sathorn Prestige Interiors ──────────────────────────────
UPDATE [dbo].[Tenants]
SET Name         = N'Sathorn Prestige Interiors',
    TenantKey    = 'sathorn-prestige-interiors',
    Domain       = 'sathornprestige.co.th',
    FromEmail    = 'admin@sathornprestige.co.th',
    ReplyToEmail = 'noreply@sathornprestige.co.th',
    UpdatedAtUtc = GETUTCDATE(),
    UpdatedBy    = 'DemoRebrand'
WHERE Id = 'BB100002-AAAA-0000-0000-000000000002';

-- ── India → Nexora Tech Solutions Pvt Ltd ──────────────────────────────
UPDATE [dbo].[Tenants]
SET Name         = N'Nexora Tech Solutions Pvt Ltd',
    TenantKey    = 'nexora-tech-solutions',
    Domain       = 'nexoratech.in',
    FromEmail    = 'admin@nexoratech.in',
    ReplyToEmail = 'noreply@nexoratech.in',
    UpdatedAtUtc = GETUTCDATE(),
    UpdatedBy    = 'DemoRebrand'
WHERE Id = 'BB100001-AAAA-0000-0000-000000000001';

-- ── Philippines → Manila Coastal Retail Group ──────────────────────────
UPDATE [dbo].[Tenants]
SET Name         = N'Manila Coastal Retail Group',
    TenantKey    = 'manila-coastal-retail',
    Domain       = 'manilacoastal.ph',
    FromEmail    = 'admin@manilacoastal.ph',
    ReplyToEmail = 'noreply@manilacoastal.ph',
    UpdatedAtUtc = GETUTCDATE(),
    UpdatedBy    = 'DemoRebrand'
WHERE Id = 'BB100003-AAAA-0000-0000-000000000003';

-- ── UAE → Al Fardan Trading LLC ─────────────────────────────────────────
UPDATE [dbo].[Tenants]
SET Name         = N'Al Fardan Trading LLC',
    TenantKey    = 'al-fardan-trading',
    Domain       = 'alfardantrading.ae',
    FromEmail    = 'admin@alfardantrading.ae',
    ReplyToEmail = 'noreply@alfardantrading.ae',
    UpdatedAtUtc = GETUTCDATE(),
    UpdatedBy    = 'DemoRebrand'
WHERE Id = 'BB100004-AAAA-0000-0000-000000000004';

-- ══════════════════════════════════════════════════════════════════════
-- STEP 2: Update the existing tenant_admin users' emails to match
-- ══════════════════════════════════════════════════════════════════════

UPDATE [dbo].[Users] SET Email = 'admin@sathornprestige.co.th', UpdatedAtUtc = GETUTCDATE(), UpdatedBy = 'DemoRebrand'
WHERE Id = 'DD100002-DD00-0000-0000-000000000002';   -- Somchai Wiriya

UPDATE [dbo].[Users] SET Email = 'admin@nexoratech.in', UpdatedAtUtc = GETUTCDATE(), UpdatedBy = 'DemoRebrand'
WHERE Id = 'DD100001-DD00-0000-0000-000000000001';   -- Rajesh Sharma

UPDATE [dbo].[Users] SET Email = 'admin@manilacoastal.ph', UpdatedAtUtc = GETUTCDATE(), UpdatedBy = 'DemoRebrand'
WHERE Id = 'DD100003-DD00-0000-0000-000000000003';   -- Maria Santos

UPDATE [dbo].[Users] SET Email = 'admin@alfardantrading.ae', UpdatedAtUtc = GETUTCDATE(), UpdatedBy = 'DemoRebrand'
WHERE Id = 'DD100004-DD00-0000-0000-000000000004';   -- Ahmed Al-Rashidi

-- Also refresh Thailand's existing sales_manager/sales_rep/viewer emails
-- to the new domain, so every user in that tenant is consistent.
UPDATE [dbo].[Users] SET Email = 'manager@sathornprestige.co.th', UpdatedAtUtc = GETUTCDATE(), UpdatedBy = 'DemoRebrand'
WHERE Id = 'DD100002-DD00-0000-0000-000000000010';   -- Nattaporn Srisai

UPDATE [dbo].[Users] SET Email = 'salesrep@sathornprestige.co.th', UpdatedAtUtc = GETUTCDATE(), UpdatedBy = 'DemoRebrand'
WHERE Id = 'DD100002-DD00-0000-0000-000000000011';   -- Kritsana Pongpanit

UPDATE [dbo].[Users] SET Email = 'viewer@sathornprestige.co.th', UpdatedAtUtc = GETUTCDATE(), UpdatedBy = 'DemoRebrand'
WHERE Id = 'DD100002-DD00-0000-0000-000000000012';   -- Sutida Charoenwong

-- ══════════════════════════════════════════════════════════════════════
-- STEP 3: New users — India (Nexora Tech Solutions)
-- Role parity with Thailand: sales_manager, sales_rep, viewer
-- ══════════════════════════════════════════════════════════════════════

INSERT INTO [dbo].[Users]
    (Id, TenantId, FirstName, LastName, Email, Phone, Department, JobTitle,
     IsActive, IsTenantAdmin, LastLoginUtc, CreatedAtUtc, CreatedBy, UpdatedAtUtc, UpdatedBy, IsDeleted)
VALUES
    ('DD100001-DD00-0000-0000-000000000010', 'BB100001-AAAA-0000-0000-000000000001',
     N'Priya', N'Nair', 'manager@nexoratech.in', NULL, N'Sales', N'Sales Manager',
     1, 0, NULL, GETUTCDATE(), 'DemoRebrand', NULL, NULL, 0),

    ('DD100001-DD00-0000-0000-000000000011', 'BB100001-AAAA-0000-0000-000000000001',
     N'Arjun', N'Mehta', 'salesrep@nexoratech.in', NULL, N'Sales', N'Sales Representative',
     1, 0, NULL, GETUTCDATE(), 'DemoRebrand', NULL, NULL, 0),

    ('DD100001-DD00-0000-0000-000000000012', 'BB100001-AAAA-0000-0000-000000000001',
     N'Deepika', N'Rao', 'viewer@nexoratech.in', NULL, N'Operations', N'Analyst',
     1, 0, NULL, GETUTCDATE(), 'DemoRebrand', NULL, NULL, 0);

INSERT INTO [dbo].[UserRoles] (Id, UserId, RoleId, AssignedAtUtc, AssignedBy)
VALUES
    (NEWID(), 'DD100001-DD00-0000-0000-000000000010', 'AA000001-0000-0000-0000-000000000001', GETUTCDATE(), 'DemoRebrand'),
    (NEWID(), 'DD100001-DD00-0000-0000-000000000011', 'AA000002-0000-0000-0000-000000000002', GETUTCDATE(), 'DemoRebrand'),
    (NEWID(), 'DD100001-DD00-0000-0000-000000000012', 'AA000003-0000-0000-0000-000000000003', GETUTCDATE(), 'DemoRebrand');

-- ══════════════════════════════════════════════════════════════════════
-- STEP 4: New users — Philippines (Manila Coastal Retail Group)
-- ══════════════════════════════════════════════════════════════════════

INSERT INTO [dbo].[Users]
    (Id, TenantId, FirstName, LastName, Email, Phone, Department, JobTitle,
     IsActive, IsTenantAdmin, LastLoginUtc, CreatedAtUtc, CreatedBy, UpdatedAtUtc, UpdatedBy, IsDeleted)
VALUES
    ('DD100003-DD00-0000-0000-000000000010', 'BB100003-AAAA-0000-0000-000000000003',
     N'Carlo', N'Villanueva', 'manager@manilacoastal.ph', NULL, N'Sales', N'Sales Manager',
     1, 0, NULL, GETUTCDATE(), 'DemoRebrand', NULL, NULL, 0),

    ('DD100003-DD00-0000-0000-000000000011', 'BB100003-AAAA-0000-0000-000000000003',
     N'Angelica', N'Reyes', 'salesrep@manilacoastal.ph', NULL, N'Sales', N'Sales Representative',
     1, 0, NULL, GETUTCDATE(), 'DemoRebrand', NULL, NULL, 0),

    ('DD100003-DD00-0000-0000-000000000012', 'BB100003-AAAA-0000-0000-000000000003',
     N'Ramon', N'Cruz', 'viewer@manilacoastal.ph', NULL, N'Operations', N'Analyst',
     1, 0, NULL, GETUTCDATE(), 'DemoRebrand', NULL, NULL, 0);

INSERT INTO [dbo].[UserRoles] (Id, UserId, RoleId, AssignedAtUtc, AssignedBy)
VALUES
    (NEWID(), 'DD100003-DD00-0000-0000-000000000010', 'AA000001-0000-0000-0000-000000000001', GETUTCDATE(), 'DemoRebrand'),
    (NEWID(), 'DD100003-DD00-0000-0000-000000000011', 'AA000002-0000-0000-0000-000000000002', GETUTCDATE(), 'DemoRebrand'),
    (NEWID(), 'DD100003-DD00-0000-0000-000000000012', 'AA000003-0000-0000-0000-000000000003', GETUTCDATE(), 'DemoRebrand');

-- ══════════════════════════════════════════════════════════════════════
-- STEP 5: New users — UAE (Al Fardan Trading LLC)
-- ══════════════════════════════════════════════════════════════════════

INSERT INTO [dbo].[Users]
    (Id, TenantId, FirstName, LastName, Email, Phone, Department, JobTitle,
     IsActive, IsTenantAdmin, LastLoginUtc, CreatedAtUtc, CreatedBy, UpdatedAtUtc, UpdatedBy, IsDeleted)
VALUES
    ('DD100004-DD00-0000-0000-000000000010', 'BB100004-AAAA-0000-0000-000000000004',
     N'Fatima', N'Al-Zaabi', 'manager@alfardantrading.ae', NULL, N'Sales', N'Sales Manager',
     1, 0, NULL, GETUTCDATE(), 'DemoRebrand', NULL, NULL, 0),

    ('DD100004-DD00-0000-0000-000000000011', 'BB100004-AAAA-0000-0000-000000000004',
     N'Omar', N'Hassan', 'salesrep@alfardantrading.ae', NULL, N'Sales', N'Sales Representative',
     1, 0, NULL, GETUTCDATE(), 'DemoRebrand', NULL, NULL, 0),

    ('DD100004-DD00-0000-0000-000000000012', 'BB100004-AAAA-0000-0000-000000000004',
     N'Layla', N'Nasser', 'viewer@alfardantrading.ae', NULL, N'Operations', N'Analyst',
     1, 0, NULL, GETUTCDATE(), 'DemoRebrand', NULL, NULL, 0);

INSERT INTO [dbo].[UserRoles] (Id, UserId, RoleId, AssignedAtUtc, AssignedBy)
VALUES
    (NEWID(), 'DD100004-DD00-0000-0000-000000000010', 'AA000001-0000-0000-0000-000000000001', GETUTCDATE(), 'DemoRebrand'),
    (NEWID(), 'DD100004-DD00-0000-0000-000000000011', 'AA000002-0000-0000-0000-000000000002', GETUTCDATE(), 'DemoRebrand'),
    (NEWID(), 'DD100004-DD00-0000-0000-000000000012', 'AA000003-0000-0000-0000-000000000003', GETUTCDATE(), 'DemoRebrand');

-- ══════════════════════════════════════════════════════════════════════
-- STEP 6: Verify before committing
-- ══════════════════════════════════════════════════════════════════════

SELECT t.Name AS Tenant, t.TenantKey, t.DefaultCurrency, t.Plan,
       u.FirstName + ' ' + u.LastName AS UserName, u.Email, r.Name AS Role
FROM [dbo].[Tenants] t
JOIN [dbo].[Users] u      ON u.TenantId = t.Id AND u.IsDeleted = 0
JOIN [dbo].[UserRoles] ur ON ur.UserId = u.Id
JOIN [dbo].[Roles] r      ON r.Id = ur.RoleId
WHERE t.Id IN (
    'BB100001-AAAA-0000-0000-000000000001',
    'BB100002-AAAA-0000-0000-000000000002',
    'BB100003-AAAA-0000-0000-000000000003',
    'BB100004-AAAA-0000-0000-000000000004'
)
ORDER BY t.Name, r.Name;

-- ── Review the SELECT output above. If it looks right: ──────────────────
COMMIT TRANSACTION;

-- ── If something looks wrong, run this instead of the COMMIT above: ─────
-- ROLLBACK TRANSACTION;
