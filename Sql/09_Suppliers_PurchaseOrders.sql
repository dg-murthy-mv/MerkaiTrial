-- =====================================================================
-- SEED: 09_Suppliers_PurchaseOrders.sql
-- 2 Suppliers per store + 2 PurchaseOrders per store
--   PO1 = Received (full delivery, ReceivedQty = OrderedQty)
--   PO2 = Ordered  (pending delivery, ReceivedQty = 0)
--
-- All GUIDs use valid hex only (0-9, a-f).
--
-- GUID Prefixes:
--   Supplier         : 9100[store][seq]
--   PurchaseOrder    : 9200[store][seq]
--   PurchaseOrderItem: 9300[store][seq]
-- =====================================================================

-- =====================================================================
-- STORE 1 — India: Brew & Bloom Cafe (AAAA0001)
-- Suppliers: FreshDeli India, BlueStar Dairy
-- =====================================================================

DECLARE @Now DATETIME2    = GETUTCDATE();
DECLARE @By  NVARCHAR(100) = 'system';

INSERT INTO [dbo].[Supplier] (
    [Id],[StoreId],[Name],[ContactPerson],[Email],[Phone],
    [Address],[City],[State],[ZipCode],[Country],
    [TaxNumber],[PaymentTerms],
    [IsActive],[IsDeleted],[CreatedAt],[CreatedBy],[UpdatedAt],[UpdatedBy]
) VALUES
(
    '91000101-0000-0000-0000-000000000000',
    'AAAA0001-0000-0000-0000-000000000000',
    'FreshDeli India Pvt Ltd','Ramesh Nair','ramesh@freshdeli.in','+91-9845001001',
    '14, Industrial Area, Peenya','Bangalore','Karnataka','560058','India',
    'GSTIN29AABCF1234A1Z5','Net 15',
    1,0,@Now,@By,@Now,NULL
),
(
    '91000102-0000-0000-0000-000000000000',
    'AAAA0001-0000-0000-0000-000000000000',
    'BlueStar Dairy & Bakery Supplies','Kavita Rao','kavita@bluestardairy.in','+91-9845001002',
    '8, Dairy Circle, Bannerghatta Road','Bangalore','Karnataka','560029','India',
    'GSTIN29AABCB5678B1Z2','Net 30',
    1,0,@Now,@By,@Now,NULL
);

-- PO1 — Received (FreshDeli, Br1)
INSERT INTO [dbo].[PurchaseOrder] (
    [Id],[StoreId],[SupplierId],[ReceivingBranchId],
    [OrderNumber],[OrderDate],[ExpectedDeliveryDate],
    [Status],[SubTotal],[TaxAmount],[TotalAmount],[Notes],
    [IsDeleted],[CreatedAt],[CreatedBy],[UpdatedAt],[UpdatedBy]
) VALUES (
    '92000101-0000-0000-0000-000000000000',
    'AAAA0001-0000-0000-0000-000000000000',
    '91000101-0000-0000-0000-000000000000',
    'DD000001-0000-0000-0000-000000000000',
    'PO-IN-0001',DATEADD(DAY,-7,@Now),DATEADD(DAY,-5,@Now),
    'Received', 2100.00,378.00,2478.00,'Weekly sandwich ingredients restock',
    0,@Now,@By,@Now,NULL
);
INSERT INTO [dbo].[PurchaseOrderItem] (
    [Id],[PurchaseOrderId],[ProductId],[Quantity],[UnitCost],[TotalCost],[ReceivedQuantity],[CreatedAt]
) VALUES
('93000101-0000-0000-0000-000000000000','92000101-0000-0000-0000-000000000000','ED010006-0000-0000-0000-000000000000',30, 70.00,2100.00,30,@Now), -- Veg Sandwich ingredients
('93000102-0000-0000-0000-000000000000','92000101-0000-0000-0000-000000000000','ED010008-0000-0000-0000-000000000000',15,100.00,1500.00,15,@Now); -- Cheesecake ingredients

-- PO2 — Ordered (BlueStar, Br1)
INSERT INTO [dbo].[PurchaseOrder] (
    [Id],[StoreId],[SupplierId],[ReceivingBranchId],
    [OrderNumber],[OrderDate],[ExpectedDeliveryDate],
    [Status],[SubTotal],[TaxAmount],[TotalAmount],[Notes],
    [IsDeleted],[CreatedAt],[CreatedBy],[UpdatedAt],[UpdatedBy]
) VALUES (
    '92000102-0000-0000-0000-000000000000',
    'AAAA0001-0000-0000-0000-000000000000',
    '91000102-0000-0000-0000-000000000000',
    'DD000001-0000-0000-0000-000000000000',
    'PO-IN-0002',@Now,DATEADD(DAY,2,@Now),
    'Ordered', 3500.00,630.00,4130.00,'Croissant dough and dairy restock',
    0,@Now,@By,@Now,NULL
);
INSERT INTO [dbo].[PurchaseOrderItem] (
    [Id],[PurchaseOrderId],[ProductId],[Quantity],[UnitCost],[TotalCost],[ReceivedQuantity],[CreatedAt]
) VALUES
('93000103-0000-0000-0000-000000000000','92000102-0000-0000-0000-000000000000','ED010007-0000-0000-0000-000000000000',50, 50.00,2500.00,0,@Now), -- Croissant
('93000104-0000-0000-0000-000000000000','92000102-0000-0000-0000-000000000000','ED010008-0000-0000-0000-000000000000',10,100.00,1000.00,0,@Now); -- Cheesecake

PRINT 'India — Brew & Bloom: suppliers & POs done';
GO

-- =====================================================================
-- STORE 2 — Thailand: Petite Farine Bakery (AAAA0002)
-- Suppliers: Siam Flour & Grain Co, Bangkok Fresh Produce
-- =====================================================================

DECLARE @Now DATETIME2    = GETUTCDATE();
DECLARE @By  NVARCHAR(100) = 'system';

INSERT INTO [dbo].[Supplier] (
    [Id],[StoreId],[Name],[ContactPerson],[Email],[Phone],
    [Address],[City],[State],[ZipCode],[Country],
    [TaxNumber],[PaymentTerms],
    [IsActive],[IsDeleted],[CreatedAt],[CreatedBy],[UpdatedAt],[UpdatedBy]
) VALUES
(
    '91000201-0000-0000-0000-000000000000',
    'AAAA0002-0000-0000-0000-000000000000',
    N'Siam Flour & Grain Co','Wanchai Pornprasit','wanchai@siamflour.co.th','+66-2-345-6789',
    '99 Rama IV Road, Klong Toei','Bangkok','Bangkok','10110','Thailand',
    '0105534012345','Net 15',
    1,0,@Now,@By,@Now,NULL
),
(
    '91000202-0000-0000-0000-000000000000',
    'AAAA0002-0000-0000-0000-000000000000',
    N'Bangkok Fresh Produce','Siriporn Kaewkla','siriporn@bkkfresh.co.th','+66-2-345-6790',
    '12 Talad Thai Market, Pathum Thani','Pathum Thani','Pathum Thani','12130','Thailand',
    '0105534056789','Net 7',
    1,0,@Now,@By,@Now,NULL
);

-- PO1 — Received (Siam Flour, Br1)
INSERT INTO [dbo].[PurchaseOrder] (
    [Id],[StoreId],[SupplierId],[ReceivingBranchId],
    [OrderNumber],[OrderDate],[ExpectedDeliveryDate],
    [Status],[SubTotal],[TaxAmount],[TotalAmount],[Notes],
    [IsDeleted],[CreatedAt],[CreatedBy],[UpdatedAt],[UpdatedBy]
) VALUES (
    '92000201-0000-0000-0000-000000000000',
    'AAAA0002-0000-0000-0000-000000000000',
    '91000201-0000-0000-0000-000000000000',
    'DD000002-0000-0000-0000-000000000000',
    'PO-TH-0001',DATEADD(DAY,-5,@Now),DATEADD(DAY,-3,@Now),
    'Received', 4200.00,294.00,4494.00,'Flour and baking supplies weekly order',
    0,@Now,@By,@Now,NULL
);
INSERT INTO [dbo].[PurchaseOrderItem] (
    [Id],[PurchaseOrderId],[ProductId],[Quantity],[UnitCost],[TotalCost],[ReceivedQuantity],[CreatedAt]
) VALUES
('93000201-0000-0000-0000-000000000000','92000201-0000-0000-0000-000000000000','ED020001-0000-0000-0000-000000000000',20, 65.00,1300.00,20,@Now), -- Sourdough
('93000202-0000-0000-0000-000000000000','92000201-0000-0000-0000-000000000000','ED020002-0000-0000-0000-000000000000',40, 28.00,1120.00,40,@Now), -- Baguette
('93000203-0000-0000-0000-000000000000','92000201-0000-0000-0000-000000000000','ED020003-0000-0000-0000-000000000000',50, 25.00,1250.00,50,@Now); -- Croissant

-- PO2 — Partially Received (Bangkok Fresh, Br2)
INSERT INTO [dbo].[PurchaseOrder] (
    [Id],[StoreId],[SupplierId],[ReceivingBranchId],
    [OrderNumber],[OrderDate],[ExpectedDeliveryDate],
    [Status],[SubTotal],[TaxAmount],[TotalAmount],[Notes],
    [IsDeleted],[CreatedAt],[CreatedBy],[UpdatedAt],[UpdatedBy]
) VALUES (
    '92000202-0000-0000-0000-000000000000',
    'AAAA0002-0000-0000-0000-000000000000',
    '91000202-0000-0000-0000-000000000000',
    'DD000022-0000-0000-0000-000000000000',
    'PO-TH-0002',DATEADD(DAY,-2,@Now),@Now,
    'PartiallyReceived',3480.00,243.60,3723.60,'Fresh fruit and tart fillings for Thonglor',
    0,@Now,@By,@Now,NULL
);
INSERT INTO [dbo].[PurchaseOrderItem] (
    [Id],[PurchaseOrderId],[ProductId],[Quantity],[UnitCost],[TotalCost],[ReceivedQuantity],[CreatedAt]
) VALUES
('93000204-0000-0000-0000-000000000000','92000202-0000-0000-0000-000000000000','ED020005-0000-0000-0000-000000000000',30, 42.00,1260.00,20,@Now), -- Lemon Tart (ordered 30, received 20)
('93000205-0000-0000-0000-000000000000','92000202-0000-0000-0000-000000000000','ED020006-0000-0000-0000-000000000000', 6,320.00,1920.00, 6,@Now); -- Mango Entremet (fully received)

PRINT 'Thailand — Petite Farine: suppliers & POs done';
GO

-- =====================================================================
-- STORE 3 — Philippines: Kape Isla Cafe (AAAA0003)
-- Suppliers: Benguet Fresh Farms, Manila Bread & Pastry Supply
-- =====================================================================

DECLARE @Now DATETIME2    = GETUTCDATE();
DECLARE @By  NVARCHAR(100) = 'system';

INSERT INTO [dbo].[Supplier] (
    [Id],[StoreId],[Name],[ContactPerson],[Email],[Phone],
    [Address],[City],[State],[ZipCode],[Country],
    [TaxNumber],[PaymentTerms],
    [IsActive],[IsDeleted],[CreatedAt],[CreatedBy],[UpdatedAt],[UpdatedBy]
) VALUES
(
    '91000301-0000-0000-0000-000000000000',
    'AAAA0003-0000-0000-0000-000000000000',
    'Benguet Fresh Farms Inc','Carlo Aguilar','carlo@benguetfresh.ph','+63-74-442-1234',
    'La Trinidad Public Market, La Trinidad','La Trinidad','Benguet','2601','Philippines',
    '123-456-789-000','Net 7',
    1,0,@Now,@By,@Now,NULL
),
(
    '91000302-0000-0000-0000-000000000000',
    'AAAA0003-0000-0000-0000-000000000000',
    'Manila Bread & Pastry Supply','Lourdes Bautista','lourdes@mbpastry.ph','+63-2-8567-1234',
    'Block 5, Divisoria Market, Tondo','Manila','Metro Manila','1012','Philippines',
    '234-567-890-000','Net 15',
    1,0,@Now,@By,@Now,NULL
);

-- PO1 — Received (Benguet Fresh, Br1)
INSERT INTO [dbo].[PurchaseOrder] (
    [Id],[StoreId],[SupplierId],[ReceivingBranchId],
    [OrderNumber],[OrderDate],[ExpectedDeliveryDate],
    [Status],[SubTotal],[TaxAmount],[TotalAmount],[Notes],
    [IsDeleted],[CreatedAt],[CreatedBy],[UpdatedAt],[UpdatedBy]
) VALUES (
    '92000301-0000-0000-0000-000000000000',
    'AAAA0003-0000-0000-0000-000000000000',
    '91000301-0000-0000-0000-000000000000',
    'DD000003-0000-0000-0000-000000000000',
    'PO-PH-0001',DATEADD(DAY,-4,@Now),DATEADD(DAY,-2,@Now),
    'Received',4840.00,580.80,5420.80,'Weekly fresh produce and protein order',
    0,@Now,@By,@Now,NULL
);
INSERT INTO [dbo].[PurchaseOrderItem] (
    [Id],[PurchaseOrderId],[ProductId],[Quantity],[UnitCost],[TotalCost],[ReceivedQuantity],[CreatedAt]
) VALUES
('93000301-0000-0000-0000-000000000000','92000301-0000-0000-0000-000000000000','ED030005-0000-0000-0000-000000000000',30, 88.00,2640.00,30,@Now), -- Tapsilog
('93000302-0000-0000-0000-000000000000','92000301-0000-0000-0000-000000000000','ED030006-0000-0000-0000-000000000000',20, 72.00,1440.00,20,@Now), -- Avocado Toast
('93000303-0000-0000-0000-000000000000','92000301-0000-0000-0000-000000000000','ED030008-0000-0000-0000-000000000000',20, 38.00, 760.00,20,@Now); -- Banana Bread

-- PO2 — Ordered (Manila Bread, Br2)
INSERT INTO [dbo].[PurchaseOrder] (
    [Id],[StoreId],[SupplierId],[ReceivingBranchId],
    [OrderNumber],[OrderDate],[ExpectedDeliveryDate],
    [Status],[SubTotal],[TaxAmount],[TotalAmount],[Notes],
    [IsDeleted],[CreatedAt],[CreatedBy],[UpdatedAt],[UpdatedBy]
) VALUES (
    '92000302-0000-0000-0000-000000000000',
    'AAAA0003-0000-0000-0000-000000000000',
    '91000302-0000-0000-0000-000000000000',
    'DD000033-0000-0000-0000-000000000000',
    'PO-PH-0002',@Now,DATEADD(DAY,3,@Now),
    'Ordered',3520.00,422.40,3942.40,'Pastry restock for BGC branch',
    0,@Now,@By,@Now,NULL
);
INSERT INTO [dbo].[PurchaseOrderItem] (
    [Id],[PurchaseOrderId],[ProductId],[Quantity],[UnitCost],[TotalCost],[ReceivedQuantity],[CreatedAt]
) VALUES
('93000304-0000-0000-0000-000000000000','92000302-0000-0000-0000-000000000000','ED030007-0000-0000-0000-000000000000',60, 32.00,1920.00,0,@Now), -- Ensaymada
('93000305-0000-0000-0000-000000000000','92000302-0000-0000-0000-000000000000','ED030008-0000-0000-0000-000000000000',40, 40.00,1600.00,0,@Now); -- Banana Bread

PRINT 'Philippines — Kape Isla: suppliers & POs done';
GO

-- =====================================================================
-- STORE 4 — UAE: Sufra Restaurant & Lounge (AAAA0004)
-- Suppliers: Al Ain Farms Fresh, Dubai Meat & Seafood Trading
-- =====================================================================

DECLARE @Now DATETIME2    = GETUTCDATE();
DECLARE @By  NVARCHAR(100) = 'system';

INSERT INTO [dbo].[Supplier] (
    [Id],[StoreId],[Name],[ContactPerson],[Email],[Phone],
    [Address],[City],[State],[ZipCode],[Country],
    [TaxNumber],[PaymentTerms],
    [IsActive],[IsDeleted],[CreatedAt],[CreatedBy],[UpdatedAt],[UpdatedBy]
) VALUES
(
    '91000401-0000-0000-0000-000000000000',
    'AAAA0004-0000-0000-0000-000000000000',
    N'Al Ain Farms Fresh LLC','Khalid Al-Nuaimi','khalid@alainfarms.ae','+971-3-765-4321',
    'Al Ain Industrial Area, Zone 8','Al Ain','Abu Dhabi','00000','UAE',
    'TRN100234567800003','Net 30',
    1,0,@Now,@By,@Now,NULL
),
(
    '91000402-0000-0000-0000-000000000000',
    'AAAA0004-0000-0000-0000-000000000000',
    N'Dubai Meat & Seafood Trading','Yousuf Al-Shamsi','yousuf@dubaimeat.ae','+971-4-321-9876',
    'Dubai Fish Market, Deira','Dubai','Dubai','00000','UAE',
    'TRN100876543200004','Net 15',
    1,0,@Now,@By,@Now,NULL
);

-- PO1 — Received (Al Ain Farms, Br1 DIFC)
INSERT INTO [dbo].[PurchaseOrder] (
    [Id],[StoreId],[SupplierId],[ReceivingBranchId],
    [OrderNumber],[OrderDate],[ExpectedDeliveryDate],
    [Status],[SubTotal],[TaxAmount],[TotalAmount],[Notes],
    [IsDeleted],[CreatedAt],[CreatedBy],[UpdatedAt],[UpdatedBy]
) VALUES (
    '92000401-0000-0000-0000-000000000000',
    'AAAA0004-0000-0000-0000-000000000000',
    '91000401-0000-0000-0000-000000000000',
    'DD000004-0000-0000-0000-000000000000',
    'PO-AE-0001',DATEADD(DAY,-3,@Now),DATEADD(DAY,-1,@Now),
    'Received',2310.00,115.50,2425.50,'Fresh produce and mezze ingredients',
    0,@Now,@By,@Now,NULL
);
INSERT INTO [dbo].[PurchaseOrderItem] (
    [Id],[PurchaseOrderId],[ProductId],[Quantity],[UnitCost],[TotalCost],[ReceivedQuantity],[CreatedAt]
) VALUES
('93000401-0000-0000-0000-000000000000','92000401-0000-0000-0000-000000000000','ED040001-0000-0000-0000-000000000000',40, 11.00, 440.00,40,@Now), -- Hummus
('93000402-0000-0000-0000-000000000000','92000401-0000-0000-0000-000000000000','ED040002-0000-0000-0000-000000000000',30, 14.00, 420.00,30,@Now), -- Fattoush
('93000403-0000-0000-0000-000000000000','92000401-0000-0000-0000-000000000000','ED040005-0000-0000-0000-000000000000',10, 68.00, 680.00,10,@Now), -- Lamb Ouzi
('93000404-0000-0000-0000-000000000000','92000401-0000-0000-0000-000000000000','ED040009-0000-0000-0000-000000000000',25, 18.00, 450.00,25,@Now); -- Kunafa

-- PO2 — Ordered (Dubai Meat & Seafood, Br2 JBR)
INSERT INTO [dbo].[PurchaseOrder] (
    [Id],[StoreId],[SupplierId],[ReceivingBranchId],
    [OrderNumber],[OrderDate],[ExpectedDeliveryDate],
    [Status],[SubTotal],[TaxAmount],[TotalAmount],[Notes],
    [IsDeleted],[CreatedAt],[CreatedBy],[UpdatedAt],[UpdatedBy]
) VALUES (
    '92000402-0000-0000-0000-000000000000',
    'AAAA0004-0000-0000-0000-000000000000',
    '91000402-0000-0000-0000-000000000000',
    'DD000044-0000-0000-0000-000000000000',
    'PO-AE-0002',@Now,DATEADD(DAY,1,@Now),
    'Ordered',2760.00,138.00,2898.00,'Fresh meat and seafood for weekend service',
    0,@Now,@By,@Now,NULL
);
INSERT INTO [dbo].[PurchaseOrderItem] (
    [Id],[PurchaseOrderId],[ProductId],[Quantity],[UnitCost],[TotalCost],[ReceivedQuantity],[CreatedAt]
) VALUES
('93000405-0000-0000-0000-000000000000','92000402-0000-0000-0000-000000000000','ED040007-0000-0000-0000-000000000000',15, 60.00, 900.00,0,@Now), -- Grilled Hammour
('93000406-0000-0000-0000-000000000000','92000402-0000-0000-0000-000000000000','ED040003-0000-0000-0000-000000000000',30, 19.00, 570.00,0,@Now), -- Kibbeh
('93000407-0000-0000-0000-000000000000','92000402-0000-0000-0000-000000000000','ED040008-0000-0000-0000-000000000000',15, 82.00,1230.00,0,@Now); -- Mixed Grill Platter

PRINT 'UAE — Sufra: suppliers & POs done';
GO

-- =====================================================================
-- VERIFY
-- =====================================================================

-- Suppliers per store
SELECT
    s.[Name]                AS Store,
    sup.[Name]              AS Supplier,
    sup.[ContactPerson],
    sup.[PaymentTerms],
    sup.[Country]
FROM      [dbo].[Supplier] sup
JOIN      [dbo].[Store]    s ON s.[Id] = sup.[StoreId]
WHERE     sup.[IsDeleted] = 0
ORDER BY  s.[Name], sup.[Name];

-- PO summary per store
SELECT
    s.[Name]                AS Store,
    sup.[Name]              AS Supplier,
    b.[Name]                AS ReceivingBranch,
    po.[OrderNumber],
    po.[Status],
    po.[TotalAmount],
    COUNT(poi.[Id])         AS LineItems,
    SUM(poi.[Quantity])     AS OrderedQty,
    SUM(poi.[ReceivedQuantity]) AS ReceivedQty
FROM      [dbo].[PurchaseOrder]     po
JOIN      [dbo].[Store]             s   ON s.[Id]   = po.[StoreId]
JOIN      [dbo].[Supplier]          sup ON sup.[Id]  = po.[SupplierId]
LEFT JOIN [dbo].[Branch]            b   ON b.[Id]   = po.[ReceivingBranchId]
JOIN      [dbo].[PurchaseOrderItem] poi ON poi.[PurchaseOrderId] = po.[Id]
WHERE     po.[IsDeleted] = 0
GROUP BY  s.[Name], sup.[Name], b.[Name], po.[OrderNumber], po.[Status], po.[TotalAmount]
ORDER BY  s.[Name], po.[OrderNumber];
GO
