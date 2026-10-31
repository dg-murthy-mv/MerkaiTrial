/* =====================================================================
   MerkaiTrial — TRIAL PLAN SETUP

   1. Retunes the trial plan: 30 days, Professional-equivalent limits.
   2. Converts ONE demo tenant to trial so expiry can be tested.
   3. Syncs TenantSettings from Plans, fixing the existing drift.

   DRY RUN BY DEFAULT — @Commit = 0 rolls back and reports.

   ─────────────────────────────────────────────────────────────────────
   WHY THE LIMITS GO UP, NOT DOWN

   The current trial (3 users / 50 leads / 20 deals / 1 GB) is shaped like
   a free-forever tier, not a trial. Competitor trials are time-limited
   with full feature access; record caps belong to free tiers.

   The practical failure: a prospect's first real action is importing
   their contact list. At 50 leads they hit the wall immediately, and the
   thing they learn about Merkai is that it stopped them. The limit that
   should bind in a trial is the CLOCK.

   Abuse is not a risk here — every trial is provisioned by hand after a
   demo, so there is no anonymous signup to exploit. These caps exist to
   stop a runaway import, not to force an upgrade.

   Note "Plan" and "Rows" are reserved words in T-SQL and are bracketed
   throughout.
   ===================================================================== */

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Commit BIT = 0;   -- ← 0 = dry run. 1 = apply.

/* Which tenant becomes the trial test subject. Change if you prefer
   another. MUST NOT be MadeeVision — that tenant holds your super-admin
   login and switching it to a trial would eventually expire your own
   access to the application. */
DECLARE @TrialTenantName NVARCHAR(200) = N'Sathorn Prestige Interiors';

DECLARE @n INT;

BEGIN TRANSACTION;
BEGIN TRY

    /* =================================================================
       1. RETUNE THE TRIAL PLAN
       ================================================================= */
    IF NOT EXISTS (SELECT 1 FROM dbo.Plans WHERE Name = 'trial')
    BEGIN
        RAISERROR('No plan named "trial" exists in dbo.Plans. Check the Name value before running.', 16, 1);
    END

    UPDATE dbo.Plans
       SET TrialDurationDays = 30,          -- was 45
           IsTrial           = 1,
           IsPublic          = 0,           -- SuperAdmin assigns it; never self-serve
           IsActive          = 1,
           MaxUsers          = 10,          -- was 3
           MaxLeads          = 2000,        -- was 50
           MaxDeals          = 500,         -- was 20
           MaxContacts       = 2000,
           MaxCompanies      = 500,
           StorageLimitBytes = 5368709120,  -- 5 GB (was 1 GB)
           MonthlyPrice      = 0,
           AnnualPrice       = 0,
           DisplayName       = 'Free Trial',
           BadgeText         = '30-Day Trial',
           UpdatedAtUtc      = SYSUTCDATETIME(),
           UpdatedBy         = 'trial-setup'
     WHERE Name = 'trial';

    SET @n = @@ROWCOUNT;
    PRINT 'Trial plan updated: ' + CAST(@n AS varchar(10)) + ' row(s).';


    /* =================================================================
       2. CONVERT ONE TENANT TO TRIAL

       TrialExpiresAt is computed FROM the plan, not hardcoded — so
       changing TrialDurationDays later is a data change, not a deploy.
       ================================================================= */
    DECLARE @TrialDays INT = (SELECT TOP 1 TrialDurationDays FROM dbo.Plans WHERE Name = 'trial');
    DECLARE @TenantId  UNIQUEIDENTIFIER =
        (SELECT TOP 1 Id FROM dbo.Tenants WHERE Name = @TrialTenantName AND IsDeleted = 0);

    IF @TenantId IS NULL
        RAISERROR('Tenant not found by that name — check dbo.Tenants.Name and adjust @TrialTenantName.', 16, 1);

    UPDATE dbo.Tenants
       SET [Plan]           = 'trial',
           TrialStartedAt   = SYSUTCDATETIME(),
           TrialExpiresAt   = DATEADD(DAY, @TrialDays, SYSUTCDATETIME()),
           TrialActivatedBy = 'trial-setup',
           TrialStatus      = 'Active',
           IsSuspended      = 0,
           IsActive         = 1,
           UpdatedAtUtc     = SYSUTCDATETIME(),
           UpdatedBy        = 'trial-setup'
     WHERE Id = @TenantId;

    PRINT 'Tenant "' + @TrialTenantName + '" moved to trial, expiring in '
          + CAST(@TrialDays AS varchar(10)) + ' days.';


    /* =================================================================
       3. SYNC TenantSettings FROM Plans

       TenantSettings is the applied snapshot each tenant is actually
       enforced against, and it has drifted: the Manila dashboard showed
       a 1,000-lead quota where the Professional plan defines 2,000.
       Provisioning writes this snapshot, so it must be correct before
       the wizard is built on top of it.
       ================================================================= */
    UPDATE ts
       SET ts.MaxUsers     = p.MaxUsers,
           ts.MaxLeads     = p.MaxLeads,
           ts.MaxDeals     = p.MaxDeals,
           ts.StorageLimit = p.StorageLimitBytes
      FROM dbo.TenantSettings ts
      JOIN dbo.Tenants t ON t.Id = ts.TenantId
      JOIN dbo.Plans   p ON p.Name = t.[Plan]
     WHERE t.IsDeleted = 0
       AND (ts.MaxUsers     <> p.MaxUsers
         OR ts.MaxLeads     <> p.MaxLeads
         OR ts.MaxDeals     <> p.MaxDeals
         OR ts.StorageLimit <> p.StorageLimitBytes);

    SET @n = @@ROWCOUNT;
    PRINT 'TenantSettings re-synced from Plans: ' + CAST(@n AS varchar(10)) + ' tenant(s) corrected.';

    /* Any tenant with NO TenantSettings row at all is a provisioning gap.
       CurrentTenantService falls back to Starter defaults when the row is
       missing, which silently grants a trial tenant MORE than its plan
       allows — failing open. Reported here so it is visible. */
    SELECT t.Name AS TenantWithNoSettingsRow, t.[Plan] AS PlanName
    FROM dbo.Tenants t
    WHERE t.IsDeleted = 0
      AND NOT EXISTS (SELECT 1 FROM dbo.TenantSettings ts WHERE ts.TenantId = t.Id);


    /* =================================================================
       4. RESULT
       ================================================================= */
    SELECT
        t.Name AS Tenant,
        t.[Plan] AS PlanName,
        t.DefaultCurrency,
        t.TrialExpiresAt,
        CASE WHEN t.TrialExpiresAt IS NULL THEN NULL
             ELSE DATEDIFF(DAY, SYSUTCDATETIME(), t.TrialExpiresAt) END AS DaysLeft,
        ts.MaxUsers, ts.MaxLeads, ts.MaxDeals,
        ts.StorageLimit / 1073741824 AS StorageGB
    FROM dbo.Tenants t
    LEFT JOIN dbo.TenantSettings ts ON ts.TenantId = t.Id
    WHERE t.IsDeleted = 0
    ORDER BY t.Name;

    IF @Commit = 1
    BEGIN
        COMMIT TRANSACTION;
        PRINT '';
        PRINT '=== COMMITTED ===';
    END
    ELSE
    BEGIN
        ROLLBACK TRANSACTION;
        PRINT '';
        PRINT '=== DRY RUN — rolled back. Set @Commit = 1 to apply. ===';
    END

END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    PRINT 'FAILED — nothing changed.';
    PRINT 'Error : ' + ERROR_MESSAGE();
    PRINT 'Line  : ' + CAST(ERROR_LINE() AS varchar(10));
END CATCH

/* =====================================================================
   CODE CHANGES THIS DEPENDS ON

   1. CurrentTenantService.GetPlanDisplayName() has no "trial" case:

          _ => "Starter"

      so the tenant you just converted will display as "Starter"
      throughout the UI. Read Plan.DisplayName from the DB instead — the
      _plan field is already loaded in that class.

   2. GetMaxLeads()/GetMaxDeals()/GetMaxUsers() fall back to STARTER
      values when TenantSettings is missing:

          return settings?.MaxLeads ?? 100;   // trial would get 100

      That fails open — a provisioning failure quietly grants more than
      the plan allows. Fall back to the Plan row, then throw.

   3. PlanHelper.GetAllPlans() omits "trial" entirely and hardcodes
      limits that contradict this table. Delete the class; read Plans.

   TESTING EXPIRY WITHOUT WAITING 30 DAYS

       UPDATE dbo.Tenants
          SET TrialExpiresAt = DATEADD(DAY, -1, SYSUTCDATETIME())
        WHERE Name = 'Sathorn Prestige Interiors';

   Then sign in as that tenant's admin: reads should work, writes should
   redirect to /TrialExpired. Set it forward again afterwards.
   ===================================================================== */
