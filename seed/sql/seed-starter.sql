-- Seed a single Starter tenant and sample records
DECLARE @now DATETIME2 = SYSUTCDATETIME();
IF NOT EXISTS (SELECT 1 FROM Companies)
BEGIN
  INSERT INTO Companies (Id, Name, Country, CreatedAtUtc, CreatedBy)
  VALUES (NEWID(),'Bang Lamung Bakery','TH', @now, '00000000-0000-0000-0000-000000000001');

  INSERT INTO Contacts (Id, FirstName, Email, Phone, PreferredLanguage, CreatedAtUtc, CreatedBy)
  VALUES (NEWID(),'Somchai','somchai@example.com','0800000000','th', @now, '00000000-0000-0000-0000-000000000001');
END
