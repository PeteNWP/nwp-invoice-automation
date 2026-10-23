USE [NWPInvoiceDev];
GO
SET XACT_ABORT ON;
BEGIN TRANSACTION;

IF COL_LENGTH(N'dbo.CapturedDocument', N'NwpSupplierOID') IS NULL
    ALTER TABLE dbo.CapturedDocument ADD NwpSupplierOID nvarchar(100) NULL;

IF COL_LENGTH(N'dbo.CapturedDocument', N'NwpSupplierReference') IS NULL
    ALTER TABLE dbo.CapturedDocument ADD NwpSupplierReference nvarchar(100) NULL;

IF OBJECT_ID(N'dbo.SupplierIdentityMapping', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SupplierIdentityMapping
    (
        Id bigint IDENTITY(1,1) NOT NULL CONSTRAINT PK_SupplierIdentityMapping PRIMARY KEY,
        ObservedSupplierName nvarchar(300) NOT NULL,
        NormalizedObservedSupplierName nvarchar(300) NOT NULL,
        NwpSupplierOID nvarchar(100) NOT NULL,
        NwpSupplierReference nvarchar(100) NULL,
        NwpSupplierName nvarchar(300) NOT NULL,
        ObservedVATNumber nvarchar(100) NULL,
        ObservedAccountNumber nvarchar(100) NULL,
        ObservedSender nvarchar(320) NULL,
        ObservedSenderDomain nvarchar(255) NULL,
        ObservedLayoutFingerprint nvarchar(500) NULL,
        IsActive bit NOT NULL CONSTRAINT DF_SupplierIdentityMapping_IsActive DEFAULT (1),
        CreatedAt datetime2(7) NOT NULL CONSTRAINT DF_SupplierIdentityMapping_CreatedAt DEFAULT SYSUTCDATETIME(),
        CreatedBy nvarchar(320) NOT NULL,
        UpdatedAt datetime2(7) NOT NULL CONSTRAINT DF_SupplierIdentityMapping_UpdatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedBy nvarchar(320) NOT NULL,
        ConfirmationCount int NOT NULL CONSTRAINT DF_SupplierIdentityMapping_ConfirmationCount DEFAULT (1),
        CONSTRAINT CK_SupplierIdentityMapping_ObservedName_NotBlank CHECK (LEN(LTRIM(RTRIM(ObservedSupplierName))) > 0),
        CONSTRAINT CK_SupplierIdentityMapping_NormalizedName_NotBlank CHECK (LEN(LTRIM(RTRIM(NormalizedObservedSupplierName))) > 0)
    );
END;

COMMIT TRANSACTION;
GO

-- One currently-approved identity per normalized observed supplier name. Historical
-- mappings may be retained by setting IsActive=0. There is deliberately no cross-database FK.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.SupplierIdentityMapping') AND name=N'UX_SupplierIdentityMapping_ActiveNormalizedName')
    CREATE UNIQUE INDEX UX_SupplierIdentityMapping_ActiveNormalizedName
        ON dbo.SupplierIdentityMapping (NormalizedObservedSupplierName)
        INCLUDE (NwpSupplierOID, NwpSupplierReference, NwpSupplierName, ConfirmationCount)
        WHERE IsActive = 1;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id=OBJECT_ID(N'dbo.CapturedDocument') AND name=N'IX_CapturedDocument_NwpSupplierOID')
    CREATE INDEX IX_CapturedDocument_NwpSupplierOID
        ON dbo.CapturedDocument (NwpSupplierOID)
        INCLUDE (NwpSupplierReference, SupplierMasterName)
        WHERE NwpSupplierOID IS NOT NULL;
GO
