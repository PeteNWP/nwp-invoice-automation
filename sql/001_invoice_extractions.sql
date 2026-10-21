SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.InvoiceExtraction', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.InvoiceExtraction (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_InvoiceExtraction PRIMARY KEY,
        DriveId nvarchar(200) NOT NULL,
        DriveItemId nvarchar(200) NOT NULL,
        SiteId nvarchar(200) NULL,
        SupplierName nvarchar(300) NULL,
        InvoiceNumber nvarchar(100) NULL,
        InvoiceDate date NULL,
        Reference nvarchar(100) NULL,
        Currency char(3) NULL,
        Total decimal(18,4) NULL,
        Confidence decimal(6,5) NULL,
        Model nvarchar(100) NULL,
        ExtractedAt datetimeoffset(7) NOT NULL,
        CreatedAt datetimeoffset(7) NOT NULL CONSTRAINT DF_InvoiceExtraction_CreatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedAt datetimeoffset(7) NOT NULL CONSTRAINT DF_InvoiceExtraction_UpdatedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT UQ_InvoiceExtraction_DriveItem UNIQUE (DriveId, DriveItemId),
        CONSTRAINT CK_InvoiceExtraction_Confidence CHECK (Confidence IS NULL OR (Confidence >= 0 AND Confidence <= 1))
    );
END;

IF OBJECT_ID(N'dbo.InvoiceExtractionLine', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.InvoiceExtractionLine (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_InvoiceExtractionLine PRIMARY KEY,
        InvoiceExtractionId bigint NOT NULL,
        LineNumber int NOT NULL,
        Sku nvarchar(100) NULL,
        Description nvarchar(1000) NOT NULL,
        Quantity decimal(18,4) NULL,
        UnitPrice decimal(18,4) NULL,
        LineTotal decimal(18,4) NULL,
        CONSTRAINT FK_InvoiceExtractionLine_Extraction FOREIGN KEY (InvoiceExtractionId) REFERENCES dbo.InvoiceExtraction(Id) ON DELETE CASCADE,
        CONSTRAINT UQ_InvoiceExtractionLine_Number UNIQUE (InvoiceExtractionId, LineNumber)
    );
END;

COMMIT TRANSACTION;
