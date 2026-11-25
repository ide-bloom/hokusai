-- Seed for the modern test app (SQLite through ODBC).
CREATE TABLE Customer (
    CustomerId INTEGER PRIMARY KEY,
    Name TEXT NOT NULL,
    City TEXT,
    CreditLimit NUMERIC NOT NULL,
    CreatedAt TEXT NOT NULL DEFAULT (datetime('now', 'localtime'))
);
CREATE TABLE Counter (Name TEXT PRIMARY KEY, NextValue INTEGER NOT NULL);
CREATE TABLE Invoice (
    InvoiceId INTEGER PRIMARY KEY,
    InvoiceNo INTEGER NOT NULL UNIQUE,
    CustomerId INTEGER NOT NULL REFERENCES Customer(CustomerId),
    InvoiceDate TEXT NOT NULL,
    Taxable INTEGER NOT NULL,
    Status TEXT NOT NULL,
    Total NUMERIC NOT NULL DEFAULT 0
);
CREATE TABLE InvoiceLine (
    LineId INTEGER PRIMARY KEY,
    InvoiceId INTEGER NOT NULL REFERENCES Invoice(InvoiceId),
    Description TEXT NOT NULL,
    Qty INTEGER NOT NULL,
    Price NUMERIC NOT NULL
);
CREATE TABLE InvoiceTax (InvoiceId INTEGER PRIMARY KEY REFERENCES Invoice(InvoiceId), Rate NUMERIC NOT NULL);
INSERT INTO Customer (CustomerId, Name, City, CreditLimit, CreatedAt) VALUES
    (1, 'Acme Ltd', 'Graz', 1000, '2026-01-01 00:00:00'), (2, 'Globex', 'Linz', 500, '2026-01-02 00:00:00'), (3, 'Initech', 'Wels', 250, '2026-01-03 00:00:00');
INSERT INTO Invoice (InvoiceId, InvoiceNo, CustomerId, InvoiceDate, Taxable, Status, Total) VALUES
    (1, 1001, 1, '2026-11-01', 1, 'Draft', 20), (2, 1002, 2, '2026-11-02', 0, 'Sent', 45),
    (3, 1003, 3, '2026-11-03', 0, 'Draft', 12), (4, 1004, 1, '2026-11-04', 1, 'Draft', 30);
INSERT INTO InvoiceLine (InvoiceId, Description, Qty, Price) VALUES
    (1, 'Widget', 2, 10), (2, 'Service', 1, 45), (3, 'Bolt', 12, 1), (4, 'Gadget', 1, 30);
INSERT INTO InvoiceTax (InvoiceId, Rate) VALUES (1, 20), (4, 20);
INSERT INTO Counter (Name, NextValue) VALUES ('Invoice', 1005);
