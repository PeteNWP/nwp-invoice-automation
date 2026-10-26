USE [NWPInvoiceDev];
GO
SET XACT_ABORT ON;
BEGIN TRANSACTION;

-- Additive review/audit state. Extracted SupplierName remains the identity observed on the
-- document; authoritative NWP supplier identity remains on CapturedDocument.
IF COL_LENGTH(N'dbo.InvoiceExtraction', N'ReviewStatus') IS NULL
    ALTER TABLE dbo.InvoiceExtraction ADD ReviewStatus tinyint NOT NULL
        CONSTRAINT DF_InvoiceExtraction_ReviewStatus DEFAULT (0);
IF COL_LENGTH(N'dbo.InvoiceExtraction', N'ReviewedAt') IS NULL
    ALTER TABLE dbo.InvoiceExtraction ADD ReviewedAt datetimeoffset(7) NULL;
IF COL_LENGTH(N'dbo.InvoiceExtraction', N'ReviewedBy') IS NULL
    ALTER TABLE dbo.InvoiceExtraction ADD ReviewedBy nvarchar(320) NULL;
IF COL_LENGTH(N'dbo.InvoiceExtraction', N'ReviewNote') IS NULL
    ALTER TABLE dbo.InvoiceExtraction ADD ReviewNote nvarchar(1000) NULL;
IF COL_LENGTH(N'dbo.InvoiceExtraction', N'ReviewRevision') IS NULL
    ALTER TABLE dbo.InvoiceExtraction ADD ReviewRevision bigint NOT NULL
        CONSTRAINT DF_InvoiceExtraction_ReviewRevision DEFAULT (0);

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name=N'CK_InvoiceExtraction_ReviewStatus')
    EXEC(N'ALTER TABLE dbo.InvoiceExtraction ADD CONSTRAINT CK_InvoiceExtraction_ReviewStatus
        CHECK (ReviewStatus IN (0, 1, 2));');

COMMIT TRANSACTION;
GO
