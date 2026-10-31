-- ============================================================
-- Sample client data - 2 clients per country (TH, IN, PH, AE)
-- Spread across different verticals and statuses so search,
-- filtering, and pagination all have something to demonstrate.
-- Run against the MadeeAutomation database.
-- ============================================================

USE MadeeAutomation;
GO

INSERT INTO dbo.ClientConfigs
    (Id, ClientName, ClientCode, Country, TimeZoneId, Currency, Vertical, Status, MerkaiIntegrationEnabled, PosFirstIntegrationEnabled)
VALUES
    -- Thailand
    (NEWID(), 'Baan Suan Cafe',            'baansuancafe',    'TH', 'Asia/Bangkok', 'THB', 1, 2, 0, 1), -- FnBRetail, Active
    (NEWID(), 'Siam Trade Partners',       'siamtrade',       'TH', 'Asia/Bangkok', 'THB', 2, 1, 1, 0), -- CrmSales, Trial

    -- India
    (NEWID(), 'Namaste Textiles',          'namastetextiles', 'IN', 'Asia/Kolkata', 'INR', 2, 2, 1, 0), -- CrmSales, Active
    (NEWID(), 'Spice Route Kitchens',      'spiceroute',      'IN', 'Asia/Kolkata', 'INR', 1, 1, 0, 1), -- FnBRetail, Trial

    -- Philippines
    (NEWID(), 'Manila Bay Bakehouse',      'manilabaybakes',  'PH', 'Asia/Manila',  'PHP', 1, 2, 0, 1), -- FnBRetail, Active
    (NEWID(), 'Cebu Fintech Solutions',    'cebufintech',     'PH', 'Asia/Manila',  'PHP', 3, 3, 0, 0), -- FinanceChit, Paused

    -- UAE
    (NEWID(), 'Dubai Gourmet Bites',       'dubaigourmet',    'AE', 'Asia/Dubai',   'AED', 1, 2, 0, 1), -- FnBRetail, Active
    (NEWID(), 'Emirates Business Support', 'emiratessupport', 'AE', 'Asia/Dubai',   'AED', 4, 1, 1, 0); -- CustomerSupport, Trial
GO

-- Quick check
SELECT ClientName, ClientCode, Country, Vertical, Status FROM dbo.ClientConfigs ORDER BY Country, ClientName;
GO
