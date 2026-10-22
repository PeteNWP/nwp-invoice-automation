SET XACT_ABORT ON;
BEGIN TRANSACTION;

-- Captured documents read from the SharePoint Incoming folder via the background
-- GraphInboxSyncWorker. The UI reads this table only; it never calls Microsoft Graph
-- on a page request. Columns are split into three groups so the periodic sync can
-- refresh capture/extraction data without overwriting a human's exception resolution:
--   1. Capture envelope (from the drive item + email metadata columns)
--   2. Extraction-derived fields (best-effort, written by the sync from dbo.InvoiceExtraction)
--   3. Resolution fields (written once by a human via /Exceptions/Resolve; sync never touches these)
IF OBJECT_ID(N'dbo.CapturedDocument', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CapturedDocument (
        Id uniqueidentifier NOT NULL CONSTRAINT PK_CapturedDocument PRIMARY KEY,
        SiteId nvarchar(200) NOT NULL,
        DriveId nvarchar(200) NOT NULL,
        GraphItemId nvarchar(200) NOT NULL,

        -- Capture envelope
        Subject nvarchar(500) NOT NULL,
        FromName nvarchar(300) NULL,
        FromAddress nvarchar(300) NULL,
        OriginalMailbox nvarchar(300) NOT NULL,
        ToAddresses nvarchar(max) NULL,
        CcAddresses nvarchar(max) NULL,
        MessageId nvarchar(300) NULL,
        InternetMessageId nvarchar(300) NULL,
        ConversationId nvarchar(300) NULL,
        EmailBodyPreview nvarchar(max) NULL,
        ReceivedAt datetimeoffset(7) NULL,
        ReceivedDate date NOT NULL,
        AttachmentName nvarchar(400) NOT NULL,
        AttachmentMime nvarchar(200) NOT NULL,

        -- Extraction-derived (refreshed by sync; not a human decision)
        HasExtraction bit NOT NULL CONSTRAINT DF_CapturedDocument_HasExtraction DEFAULT 0,
        DetectedSupplier nvarchar(300) NULL,
        ExtractedInvoiceNumber nvarchar(100) NULL,
        ExtractedInvoiceDate date NULL,
        ExtractedReference nvarchar(100) NULL,
        ExtractedTotal decimal(18,4) NULL,
        ExtractedCurrency char(3) NULL,

        -- Resolution (set once by a human; the sync must never overwrite these)
        ResolvedStatus int NULL,
        ResolvedDocumentType int NULL,
        SupplierMasterName nvarchar(300) NULL,
        ResolvedInvoiceNumber nvarchar(100) NULL,
        ResolvedInvoiceDate date NULL,
        ResolvedReference nvarchar(100) NULL,
        ResolvedTotal decimal(18,4) NULL,
        ResolvedCurrency char(3) NULL,
        AssignedQueue nvarchar(100) NULL,
        WorkflowOwner nvarchar(200) NULL,
        ReviewerNote nvarchar(1000) NULL,
        ExceptionReason nvarchar(500) NULL,
        ResolvedAt datetimeoffset(7) NULL,

        IsDeleted bit NOT NULL CONSTRAINT DF_CapturedDocument_IsDeleted DEFAULT 0,
        CreatedAt datetimeoffset(7) NOT NULL CONSTRAINT DF_CapturedDocument_CreatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedAt datetimeoffset(7) NOT NULL CONSTRAINT DF_CapturedDocument_UpdatedAt DEFAULT SYSUTCDATETIME(),
        LastSeenInGraphAt datetimeoffset(7) NOT NULL,

        CONSTRAINT UQ_CapturedDocument_DriveItem UNIQUE (DriveId, GraphItemId)
    );
    CREATE INDEX IX_CapturedDocument_ReceivedAt ON dbo.CapturedDocument (IsDeleted, ReceivedAt DESC);
END;

-- Bookkeeping for the background sync worker. DeltaLink holds the persisted Microsoft Graph
-- delta cursor for the Incoming folder: null means the next run performs a full initial
-- delta pass; a stored value means the next run follows that link and only sees changes.
IF OBJECT_ID(N'dbo.GraphSyncState', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.GraphSyncState (
        SyncKey nvarchar(100) NOT NULL CONSTRAINT PK_GraphSyncState PRIMARY KEY,
        DeltaLink nvarchar(max) NULL,
        LastRunStartedAt datetimeoffset(7) NULL,
        LastRunCompletedAt datetimeoffset(7) NULL,
        LastRunSucceeded bit NULL,
        LastError nvarchar(max) NULL
    );
END;

-- Widen DeltaLink for installs created before this column was nvarchar(max): Graph delta
-- URLs carry an opaque skip token that can exceed the original 2000-character cap.
IF EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID(N'dbo.GraphSyncState') AND name = N'DeltaLink' AND max_length <> -1
)
BEGIN
    ALTER TABLE dbo.GraphSyncState ALTER COLUMN DeltaLink nvarchar(max) NULL;
END;

COMMIT TRANSACTION;
