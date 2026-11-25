-- Seed for the legacy test app (SQL Server LocalDB). Run against an empty database.
CREATE TABLE Customer (
    CustomerId int IDENTITY(1,1) PRIMARY KEY,
    Name nvarchar(100) NOT NULL,
    City nvarchar(50) NULL,
    CreditLimit decimal(12,2) NOT NULL,
    CreatedAt datetime2 NOT NULL CONSTRAINT DF_Customer_CreatedAt DEFAULT SYSDATETIME()
);
CREATE TABLE Counter (Name nvarchar(30) PRIMARY KEY, NextValue int NOT NULL);
CREATE TABLE Invoice (
    InvoiceId int IDENTITY(1,1) PRIMARY KEY,
    InvoiceNo int NOT NULL UNIQUE,
    CustomerId int NOT NULL REFERENCES Customer(CustomerId),
    InvoiceDate date NOT NULL,
    Taxable bit NOT NULL,
    Status nvarchar(20) NOT NULL,
    Total decimal(12,2) NOT NULL CONSTRAINT DF_Invoice_Total DEFAULT 0
);
CREATE TABLE InvoiceLine (
    LineId int IDENTITY(1,1) PRIMARY KEY,
    InvoiceId int NOT NULL REFERENCES Invoice(InvoiceId),
    Description nvarchar(200) NOT NULL,
    Qty int NOT NULL,
    Price decimal(12,2) NOT NULL
);
CREATE TABLE InvoiceTax (InvoiceId int PRIMARY KEY REFERENCES Invoice(InvoiceId), Rate decimal(5,2) NOT NULL);
GO
SET IDENTITY_INSERT Customer ON;
INSERT INTO Customer (CustomerId, Name, City, CreditLimit, CreatedAt) VALUES
    (1, N'Acme Ltd', N'Graz', 1000, '2026-01-01'), (2, N'Globex', N'Linz', 500, '2026-01-02'), (3, N'Initech', N'Wels', 250, '2026-01-03');
SET IDENTITY_INSERT Customer OFF;
SET IDENTITY_INSERT Invoice ON;
INSERT INTO Invoice (InvoiceId, InvoiceNo, CustomerId, InvoiceDate, Taxable, Status, Total) VALUES
    (1, 1001, 1, '2026-11-01', 1, N'Draft', 20), (2, 1002, 2, '2026-11-02', 0, N'Sent', 45),
    (3, 1003, 3, '2026-11-03', 0, N'Draft', 12), (4, 1004, 1, '2026-11-04', 1, N'Draft', 30);
SET IDENTITY_INSERT Invoice OFF;
INSERT INTO InvoiceLine (InvoiceId, Description, Qty, Price) VALUES
    (1, N'Widget', 2, 10), (2, N'Service', 1, 45), (3, N'Bolt', 12, 1), (4, N'Gadget', 1, 30);
INSERT INTO InvoiceTax (InvoiceId, Rate) VALUES (1, 20), (4, 20);
INSERT INTO Counter (Name, NextValue) VALUES (N'Invoice', 1005);
