IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
GO
USE [NWPInvoiceDev];
GO
SET XACT_ABORT ON;
BEGIN TRANSACTION;

-- Retain the full Copilot Studio JSON response against each extraction so we can
-- diagnose mismatches (e.g. a response belonging to a different document) instead
-- of guessing. Nullable because historical rows predate capture.
IF COL_LENGTH(N'dbo.InvoiceExtraction', N'RawResponse') IS NULL
    ALTER TABLE dbo.InvoiceExtraction ADD RawResponse nvarchar(max) NULL;

COMMIT TRANSACTION;
GO
