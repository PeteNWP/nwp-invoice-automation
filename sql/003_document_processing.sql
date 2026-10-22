SET XACT_ABORT ON;
BEGIN TRANSACTION;

-- Per-document processing state for the Copilot Studio extraction worker.
-- These columns are owned exclusively by InvoiceProcessingWorker. The Graph inbox
-- sync MERGE (dbo.CapturedDocument upsert) does NOT list them, so a sync cycle can
-- never reset a document's processing status or a human decision. Extraction RESULTS
-- live in dbo.InvoiceExtraction / dbo.InvoiceExtractionLine (migration 001); this is
-- only the queue/audit state that drives the worker.

IF COL_LENGTH(N'dbo.CapturedDocument', N'ProcessingStatus') IS NULL
    ALTER TABLE dbo.CapturedDocument ADD ProcessingStatus tinyint NOT NULL
        CONSTRAINT DF_CapturedDocument_ProcessingStatus DEFAULT 0;   -- 0 Pending, 1 InProgress, 2 Extracted, 3 Failed, 4 Skipped

IF COL_LENGTH(N'dbo.CapturedDocument', N'ProcessingAttempts') IS NULL
    ALTER TABLE dbo.CapturedDocument ADD ProcessingAttempts int NOT NULL
        CONSTRAINT DF_CapturedDocument_ProcessingAttempts DEFAULT 0;

IF COL_LENGTH(N'dbo.CapturedDocument', N'ProcessingStartedAt') IS NULL
    ALTER TABLE dbo.CapturedDocument ADD ProcessingStartedAt datetimeoffset(7) NULL;

IF COL_LENGTH(N'dbo.CapturedDocument', N'LastProcessedAt') IS NULL
    ALTER TABLE dbo.CapturedDocument ADD LastProcessedAt datetimeoffset(7) NULL;

IF COL_LENGTH(N'dbo.CapturedDocument', N'LastProcessingError') IS NULL
    ALTER TABLE dbo.CapturedDocument ADD LastProcessingError nvarchar(max) NULL;

IF COL_LENGTH(N'dbo.CapturedDocument', N'ClassifiedDocumentType') IS NULL
    ALTER TABLE dbo.CapturedDocument ADD ClassifiedDocumentType nvarchar(60) NULL;  -- Copilot's returned documentType string

COMMIT TRANSACTION;
GO

-- Index the worker's claim query: pending/retryable rows, newest first.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CapturedDocument_Processing' AND object_id = OBJECT_ID(N'dbo.CapturedDocument'))
    CREATE INDEX IX_CapturedDocument_Processing
        ON dbo.CapturedDocument (ProcessingStatus, ReceivedDate DESC)
        WHERE IsDeleted = 0;
GO
