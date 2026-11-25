-- Seed for the Access test app (ACE OLE DB). One statement per batch: Jet/ACE executes a single statement per command.
CREATE TABLE Customer (CustomerId COUNTER PRIMARY KEY, Name TEXT(100) NOT NULL, City TEXT(50), CreditLimit CURRENCY NOT NULL, CreatedAt DATETIME DEFAULT Now())
GO
CREATE TABLE [Counter] (Name TEXT(30) PRIMARY KEY, NextValue INTEGER NOT NULL)
GO
CREATE TABLE Invoice (InvoiceId COUNTER PRIMARY KEY, InvoiceNo INTEGER NOT NULL, CustomerId INTEGER NOT NULL REFERENCES Customer(CustomerId), InvoiceDate DATETIME NOT NULL, Taxable BIT NOT NULL, Status TEXT(20) NOT NULL, Total CURRENCY DEFAULT 0)
GO
CREATE TABLE InvoiceLine (LineId COUNTER PRIMARY KEY, InvoiceId INTEGER NOT NULL REFERENCES Invoice(InvoiceId), Description TEXT(200) NOT NULL, Qty INTEGER NOT NULL, Price CURRENCY NOT NULL)
GO
CREATE TABLE InvoiceTax (InvoiceId INTEGER PRIMARY KEY REFERENCES Invoice(InvoiceId), Rate DOUBLE NOT NULL)
GO
INSERT INTO Customer (Name, City, CreditLimit, CreatedAt) VALUES ('Acme Ltd', 'Graz', 1000, #2026-01-01#)
GO
INSERT INTO Customer (Name, City, CreditLimit, CreatedAt) VALUES ('Globex', 'Linz', 500, #2026-01-02#)
GO
INSERT INTO Customer (Name, City, CreditLimit, CreatedAt) VALUES ('Initech', 'Wels', 250, #2026-01-03#)
GO
INSERT INTO Invoice (InvoiceNo, CustomerId, InvoiceDate, Taxable, Status, Total) VALUES (1001, 1, #2026-11-01#, True, 'Draft', 20)
GO
INSERT INTO Invoice (InvoiceNo, CustomerId, InvoiceDate, Taxable, Status, Total) VALUES (1002, 2, #2026-11-02#, False, 'Sent', 45)
GO
INSERT INTO Invoice (InvoiceNo, CustomerId, InvoiceDate, Taxable, Status, Total) VALUES (1003, 3, #2026-11-03#, False, 'Draft', 12)
GO
INSERT INTO Invoice (InvoiceNo, CustomerId, InvoiceDate, Taxable, Status, Total) VALUES (1004, 1, #2026-11-04#, True, 'Draft', 30)
GO
INSERT INTO InvoiceLine (InvoiceId, Description, Qty, Price) VALUES (1, 'Widget', 2, 10)
GO
INSERT INTO InvoiceLine (InvoiceId, Description, Qty, Price) VALUES (2, 'Service', 1, 45)
GO
INSERT INTO InvoiceLine (InvoiceId, Description, Qty, Price) VALUES (3, 'Bolt', 12, 1)
GO
INSERT INTO InvoiceLine (InvoiceId, Description, Qty, Price) VALUES (4, 'Gadget', 1, 30)
GO
INSERT INTO InvoiceTax (InvoiceId, Rate) VALUES (1, 20)
GO
INSERT INTO InvoiceTax (InvoiceId, Rate) VALUES (4, 20)
GO
INSERT INTO [Counter] (Name, NextValue) VALUES ('Invoice', 1005)
