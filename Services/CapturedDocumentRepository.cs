using System.Data;
using Microsoft.Data.SqlClient;
using Nwp.InvoiceAutomation.Web.Models;

namespace Nwp.InvoiceAutomation.Web.Services;

public sealed class CapturedDocumentRepository : ICapturedDocumentRepository
{
    private readonly string? _connectionString;

    public CapturedDocumentRepository(IConfiguration configuration) =>
        _connectionString = configuration.GetConnectionString("InvoiceAutomation");

    private SqlConnection CreateConnection() => !string.IsNullOrWhiteSpace(_connectionString)
        ? new SqlConnection(_connectionString)
        : throw new InvalidOperationException("ConnectionStrings:InvoiceAutomation is not configured.");

    public async Task<string?> GetDeltaLinkAsync(string syncKey, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("SELECT DeltaLink FROM dbo.GraphSyncState WHERE SyncKey=@SyncKey", connection);
        Add(command, "@SyncKey", SqlDbType.NVarChar, 100, syncKey);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? null : (string)result;
    }

    public async Task SetDeltaLinkAsync(string syncKey, string? deltaLink, CancellationToken cancellationToken)
    {
        const string sql = """
            MERGE dbo.GraphSyncState AS target
            USING (SELECT @SyncKey AS SyncKey) AS src ON target.SyncKey = src.SyncKey
            WHEN MATCHED THEN UPDATE SET DeltaLink=@DeltaLink
            WHEN NOT MATCHED THEN INSERT (SyncKey, DeltaLink) VALUES (@SyncKey, @DeltaLink);
            """;
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        Add(command, "@SyncKey", SqlDbType.NVarChar, 100, syncKey);
        Add(command, "@DeltaLink", SqlDbType.NVarChar, -1, Clean(deltaLink));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ApplyDeltaAsync(
        string driveId,
        IReadOnlyList<CapturedDocument> upserts,
        IReadOnlyList<(string DriveId, string GraphItemId)> deletions,
        bool reconcileMissingAsDeleted,
        CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var document in upserts)
            {
                await UpsertCaptureAsync(connection, transaction, driveId, document, cancellationToken);
            }

            foreach (var (deletedDriveId, graphItemId) in deletions)
            {
                await MarkDeletedByGraphItemAsync(connection, transaction, deletedDriveId, graphItemId, cancellationToken);
            }

            // Only valid on an initial full delta pass, where `upserts` is the complete current
            // state of the folder (Graph returns every existing item when there is no prior
            // cursor). Incremental runs only ever see items that actually changed, so reconciling
            // against "unseen" there would wrongly soft-delete the rest of the folder.
            // Defensive: an empty initial pass is more likely a transient failure upstream than
            // a genuinely empty Incoming folder, so never soft-delete everything on an empty batch.
            if (reconcileMissingAsDeleted && upserts.Count > 0)
            {
                await MarkMissingAsDeletedAsync(connection, transaction, driveId, upserts, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task MarkDeletedByGraphItemAsync(
        SqlConnection connection, SqlTransaction transaction, string driveId, string graphItemId, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE dbo.CapturedDocument SET IsDeleted=1, UpdatedAt=SYSUTCDATETIME()
            WHERE DriveId=@DriveId AND GraphItemId=@GraphItemId AND IsDeleted=0
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        Add(command, "@DriveId", SqlDbType.NVarChar, 200, driveId);
        Add(command, "@GraphItemId", SqlDbType.NVarChar, 200, graphItemId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertCaptureAsync(
        SqlConnection connection, SqlTransaction transaction, string driveId, CapturedDocument document, CancellationToken cancellationToken)
    {
        const string sql = """
            MERGE dbo.CapturedDocument AS target
            USING (SELECT @Id AS Id) AS src ON target.Id = src.Id
            WHEN MATCHED THEN UPDATE SET
                SiteId=@SiteId, DriveId=@DriveId, GraphItemId=@GraphItemId,
                Subject=@Subject, FromName=@FromName, FromAddress=@FromAddress, OriginalMailbox=@OriginalMailbox,
                ToAddresses=@ToAddresses, CcAddresses=@CcAddresses, MessageId=@MessageId, InternetMessageId=@InternetMessageId,
                ConversationId=@ConversationId, EmailBodyPreview=@EmailBodyPreview, ReceivedAt=@ReceivedAt, ReceivedDate=@ReceivedDate,
                AttachmentName=@AttachmentName, AttachmentMime=@AttachmentMime,
                HasExtraction=@HasExtraction, DetectedSupplier=@DetectedSupplier, ExtractedInvoiceNumber=@ExtractedInvoiceNumber,
                ExtractedInvoiceDate=@ExtractedInvoiceDate, ExtractedReference=@ExtractedReference, ExtractedTotal=@ExtractedTotal,
                ExtractedCurrency=@ExtractedCurrency, IsDeleted=0, UpdatedAt=SYSUTCDATETIME(), LastSeenInGraphAt=SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT
                (Id, SiteId, DriveId, GraphItemId, Subject, FromName, FromAddress, OriginalMailbox, ToAddresses, CcAddresses,
                 MessageId, InternetMessageId, ConversationId, EmailBodyPreview, ReceivedAt, ReceivedDate, AttachmentName, AttachmentMime,
                 HasExtraction, DetectedSupplier, ExtractedInvoiceNumber, ExtractedInvoiceDate, ExtractedReference, ExtractedTotal, ExtractedCurrency,
                 IsDeleted, LastSeenInGraphAt)
            VALUES
                (@Id, @SiteId, @DriveId, @GraphItemId, @Subject, @FromName, @FromAddress, @OriginalMailbox, @ToAddresses, @CcAddresses,
                 @MessageId, @InternetMessageId, @ConversationId, @EmailBodyPreview, @ReceivedAt, @ReceivedDate, @AttachmentName, @AttachmentMime,
                 @HasExtraction, @DetectedSupplier, @ExtractedInvoiceNumber, @ExtractedInvoiceDate, @ExtractedReference, @ExtractedTotal, @ExtractedCurrency,
                 0, SYSUTCDATETIME());
            """;

        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@Id", SqlDbType.UniqueIdentifier).Value = document.Id;
        Add(command, "@SiteId", SqlDbType.NVarChar, 200, document.GraphSiteId ?? "");
        Add(command, "@DriveId", SqlDbType.NVarChar, 200, driveId);
        Add(command, "@GraphItemId", SqlDbType.NVarChar, 200, document.GraphItemId ?? "");
        Add(command, "@Subject", SqlDbType.NVarChar, 500, document.Subject);
        Add(command, "@FromName", SqlDbType.NVarChar, 300, Clean(document.FromName));
        Add(command, "@FromAddress", SqlDbType.NVarChar, 300, Clean(document.FromAddress));
        Add(command, "@OriginalMailbox", SqlDbType.NVarChar, 300, document.OriginalMailbox);
        Add(command, "@ToAddresses", SqlDbType.NVarChar, -1, Clean(document.ToAddresses));
        Add(command, "@CcAddresses", SqlDbType.NVarChar, -1, Clean(document.CcAddresses));
        Add(command, "@MessageId", SqlDbType.NVarChar, 300, Clean(document.MessageId));
        Add(command, "@InternetMessageId", SqlDbType.NVarChar, 300, Clean(document.InternetMessageId));
        Add(command, "@ConversationId", SqlDbType.NVarChar, 300, Clean(document.ConversationId));
        Add(command, "@EmailBodyPreview", SqlDbType.NVarChar, -1, Clean(document.EmailBodyPreview));
        Add(command, "@ReceivedAt", SqlDbType.DateTimeOffset, null, document.ReceivedAt);
        Add(command, "@ReceivedDate", SqlDbType.Date, null, document.ReceivedDate.ToDateTime(TimeOnly.MinValue));
        Add(command, "@AttachmentName", SqlDbType.NVarChar, 400, document.AttachmentName);
        Add(command, "@AttachmentMime", SqlDbType.NVarChar, 200, document.AttachmentMime);
        command.Parameters.Add("@HasExtraction", SqlDbType.Bit).Value = document.Extraction is not null;
        Add(command, "@DetectedSupplier", SqlDbType.NVarChar, 300, Clean(document.DetectedSupplier));
        Add(command, "@ExtractedInvoiceNumber", SqlDbType.NVarChar, 100, Clean(document.InvoiceNumber));
        Add(command, "@ExtractedInvoiceDate", SqlDbType.Date, null, document.InvoiceDate?.ToDateTime(TimeOnly.MinValue));
        Add(command, "@ExtractedReference", SqlDbType.NVarChar, 100, Clean(document.BatchOrReference));
        AddDecimal(command, "@ExtractedTotal", document.Total, 18, 4);
        Add(command, "@ExtractedCurrency", SqlDbType.Char, 3, Clean(document.Currency)?.ToUpperInvariant());

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task MarkMissingAsDeletedAsync(
        SqlConnection connection, SqlTransaction transaction, string driveId, IReadOnlyList<CapturedDocument> currentFromGraph, CancellationToken cancellationToken)
    {
        var seenIds = currentFromGraph.Select(d => d.Id).Distinct().ToList();
        var parameterNames = seenIds.Select((_, i) => $"@seen{i}").ToList();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        for (var i = 0; i < seenIds.Count; i++)
        {
            command.Parameters.Add(parameterNames[i], SqlDbType.UniqueIdentifier).Value = seenIds[i];
        }
        command.Parameters.Add("@DriveId", SqlDbType.NVarChar, 200).Value = driveId;
        command.CommandText = $"""
            UPDATE dbo.CapturedDocument SET IsDeleted=1, UpdatedAt=SYSUTCDATETIME()
            WHERE DriveId=@DriveId AND IsDeleted=0 AND Id NOT IN ({string.Join(",", parameterNames)})
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CapturedDocument>> GetAllAsync(CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand($"{SelectColumns} WHERE IsDeleted = 0", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var documents = new List<CapturedDocument>();
        while (await reader.ReadAsync(cancellationToken))
        {
            documents.Add(Map(reader));
        }

        return documents
            .OrderByDescending(d => d.ReceivedAt ?? new DateTimeOffset(d.ReceivedDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero))
            .ThenBy(d => d.AttachmentName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<CapturedDocument?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand($"{SelectColumns} WHERE IsDeleted = 0 AND Id = @Id", connection);
        command.Parameters.Add("@Id", SqlDbType.UniqueIdentifier).Value = id;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Map(reader) : null;
    }

    public async Task<int> ResolveExceptionAsync(Guid id, ExceptionResolution resolution, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE dbo.CapturedDocument SET
                ResolvedStatus=@Status, ResolvedDocumentType=@DocumentType, SupplierMasterName=@SupplierMasterName,
                NwpSupplierOID=@NwpSupplierOID, NwpSupplierReference=@NwpSupplierReference,
                ResolvedInvoiceNumber=@InvoiceNumber, ResolvedInvoiceDate=@InvoiceDate, ResolvedReference=@Reference,
                ResolvedTotal=@Total, ResolvedCurrency=@Currency, AssignedQueue=@AssignedQueue, WorkflowOwner=@WorkflowOwner,
                ReviewerNote=@ReviewerNote, ExceptionReason=NULL, ResolvedAt=SYSUTCDATETIME(), UpdatedAt=SYSUTCDATETIME()
            WHERE Id=@Id
            """;
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@Id", SqlDbType.UniqueIdentifier).Value = id;
        var status = string.Equals(resolution.AssignedQueue, "Ignored", StringComparison.OrdinalIgnoreCase)
            ? ClassificationStatus.Ignored
            : resolution.MarkReviewed ? ClassificationStatus.Classified : ClassificationStatus.ReviewRequired;
        command.Parameters.Add("@Status", SqlDbType.Int).Value = (int)status;
        command.Parameters.Add("@DocumentType", SqlDbType.Int).Value = (int)resolution.DocumentType;
        Add(command, "@SupplierMasterName", SqlDbType.NVarChar, 300, Clean(resolution.SupplierMasterName));
        Add(command, "@NwpSupplierOID", SqlDbType.NVarChar, 100, Clean(resolution.NwpSupplierOid));
        Add(command, "@NwpSupplierReference", SqlDbType.NVarChar, 100, Clean(resolution.NwpSupplierReference));
        Add(command, "@InvoiceNumber", SqlDbType.NVarChar, 100, Clean(resolution.InvoiceNumber));
        Add(command, "@InvoiceDate", SqlDbType.Date, null, resolution.InvoiceDate?.ToDateTime(TimeOnly.MinValue));
        Add(command, "@Reference", SqlDbType.NVarChar, 100, Clean(resolution.BatchOrReference));
        AddDecimal(command, "@Total", resolution.Total, 18, 4);
        Add(command, "@Currency", SqlDbType.Char, 3, Clean(resolution.Currency)?.ToUpperInvariant());
        Add(command, "@AssignedQueue", SqlDbType.NVarChar, 100, Clean(resolution.AssignedQueue));
        Add(command, "@WorkflowOwner", SqlDbType.NVarChar, 200, Clean(resolution.WorkflowOwner));
        Add(command, "@ReviewerNote", SqlDbType.NVarChar, 1000, Clean(resolution.ResolutionNote));
        await command.ExecuteNonQueryAsync(cancellationToken);

        var retroactivelyUpdated = 0;
        if (resolution.TaughtSupplierAlias && !string.IsNullOrWhiteSpace(resolution.ObservedSupplierName))
        {
            await UpsertSupplierIdentityMappingAsync(connection, transaction, resolution, cancellationToken);
            retroactivelyUpdated = await BackfillSupplierIdentityAsync(connection, transaction, id, resolution, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return retroactivelyUpdated;
    }

    private static async Task UpsertSupplierIdentityMappingAsync(SqlConnection connection, SqlTransaction transaction,
        ExceptionResolution resolution, CancellationToken cancellationToken)
    {
        const string sql = """
            DECLARE @Normalized nvarchar(300) = LOWER(LTRIM(RTRIM(@ObservedName)));
            UPDATE dbo.SupplierIdentityMapping WITH (UPDLOCK, SERIALIZABLE)
               SET ObservedSupplierName=@ObservedName, NwpSupplierOID=@NwpSupplierOID,
                   NwpSupplierReference=@NwpSupplierReference, NwpSupplierName=@NwpSupplierName,
                   ObservedSender=@ObservedSender, ObservedSenderDomain=@ObservedSenderDomain,
                   IsActive=1, UpdatedAt=SYSUTCDATETIME(), UpdatedBy=@UpdatedBy,
                   ConfirmationCount=ConfirmationCount + 1
             WHERE NormalizedObservedSupplierName=@Normalized AND IsActive=1;
            IF @@ROWCOUNT = 0
                INSERT dbo.SupplierIdentityMapping
                    (ObservedSupplierName, NormalizedObservedSupplierName, NwpSupplierOID, NwpSupplierReference,
                     NwpSupplierName, ObservedSender, ObservedSenderDomain, IsActive, CreatedBy, UpdatedBy, ConfirmationCount)
                VALUES
                    (@ObservedName, @Normalized, @NwpSupplierOID, @NwpSupplierReference,
                     @NwpSupplierName, @ObservedSender, @ObservedSenderDomain, 1, @UpdatedBy, @UpdatedBy, 1);
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        Add(command, "@ObservedName", SqlDbType.NVarChar, 300, Clean(resolution.ObservedSupplierName));
        Add(command, "@NwpSupplierOID", SqlDbType.NVarChar, 100, Clean(resolution.NwpSupplierOid));
        Add(command, "@NwpSupplierReference", SqlDbType.NVarChar, 100, Clean(resolution.NwpSupplierReference));
        Add(command, "@NwpSupplierName", SqlDbType.NVarChar, 300, Clean(resolution.SupplierMasterName));
        Add(command, "@ObservedSender", SqlDbType.NVarChar, 320, Clean(resolution.ObservedSender));
        Add(command, "@ObservedSenderDomain", SqlDbType.NVarChar, 255, Clean(resolution.ObservedSenderDomain));
        Add(command, "@UpdatedBy", SqlDbType.NVarChar, 320, Clean(resolution.ResolvedBy));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> BackfillSupplierIdentityAsync(SqlConnection connection, SqlTransaction transaction,
        Guid resolvedDocumentId, ExceptionResolution resolution, CancellationToken cancellationToken)
    {
        // ResolvedAt distinguishes a human decision from an automatically applied identity.
        // ReviewStatus=0 protects corrected/approved extraction history, while the captured-
        // document status filter excludes classified and ignored workflow history.
        const string sql = """
            UPDATE d
               SET SupplierMasterName=@NwpSupplierName,
                   NwpSupplierOID=@NwpSupplierOID,
                   NwpSupplierReference=@NwpSupplierReference,
                   UpdatedAt=SYSUTCDATETIME()
              FROM dbo.CapturedDocument d
              LEFT JOIN dbo.InvoiceExtraction e
                ON e.DriveId=d.DriveId AND e.DriveItemId=d.GraphItemId
             WHERE d.Id<>@ResolvedDocumentId
               AND d.IsDeleted=0
               AND d.ResolvedAt IS NULL
               AND (d.ResolvedStatus IS NULL OR d.ResolvedStatus IN (0,2,4))
               AND (e.Id IS NULL OR e.ReviewStatus=0)
               AND LOWER(LTRIM(RTRIM(COALESCE(e.SupplierName,d.DetectedSupplier))))=@Normalized;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@ResolvedDocumentId", SqlDbType.UniqueIdentifier).Value = resolvedDocumentId;
        Add(command, "@Normalized", SqlDbType.NVarChar, 300,
            SupplierIdentityMappingPolicy.Normalize(resolution.ObservedSupplierName));
        Add(command, "@NwpSupplierOID", SqlDbType.NVarChar, 100, Clean(resolution.NwpSupplierOid));
        Add(command, "@NwpSupplierReference", SqlDbType.NVarChar, 100, Clean(resolution.NwpSupplierReference));
        Add(command, "@NwpSupplierName", SqlDbType.NVarChar, 300, Clean(resolution.SupplierMasterName));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RecordSyncRunAsync(string syncKey, DateTimeOffset startedAt, bool succeeded, string? error, CancellationToken cancellationToken)
    {
        const string sql = """
            MERGE dbo.GraphSyncState AS target
            USING (SELECT @SyncKey AS SyncKey) AS src ON target.SyncKey = src.SyncKey
            WHEN MATCHED THEN UPDATE SET
                LastRunStartedAt=@StartedAt, LastRunCompletedAt=SYSUTCDATETIME(), LastRunSucceeded=@Succeeded, LastError=@Error
            WHEN NOT MATCHED THEN INSERT (SyncKey, LastRunStartedAt, LastRunCompletedAt, LastRunSucceeded, LastError)
                VALUES (@SyncKey, @StartedAt, SYSUTCDATETIME(), @Succeeded, @Error);
            """;
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        Add(command, "@SyncKey", SqlDbType.NVarChar, 100, syncKey);
        command.Parameters.Add("@StartedAt", SqlDbType.DateTimeOffset).Value = startedAt;
        command.Parameters.Add("@Succeeded", SqlDbType.Bit).Value = succeeded;
        Add(command, "@Error", SqlDbType.NVarChar, -1, Clean(error));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ProcessingCandidate>> ClaimForProcessingAsync(
        int batchSize, DateOnly? cutoff, int maxAttempts, CancellationToken cancellationToken)
    {
        // CTE + UPDATE ... OUTPUT claims and locks the batch in one atomic statement.
        // UPDLOCK/ROWLOCK keep the claim exclusive. Do not use READPAST here: databases with
        // READ_COMMITTED_SNAPSHOT enabled reject that hint under the default isolation level.
        const string sql = """
            WITH batch AS (
                SELECT TOP (@BatchSize) Id, SiteId, DriveId, GraphItemId, AttachmentName, AttachmentMime,
                                        ProcessingStatus, ProcessingAttempts, ProcessingStartedAt, UpdatedAt
                FROM dbo.CapturedDocument WITH (UPDLOCK, ROWLOCK)
                WHERE IsDeleted = 0
                  AND DriveId IS NOT NULL AND GraphItemId IS NOT NULL
                  AND (ProcessingStatus = 0 OR (ProcessingStatus = 3 AND ProcessingAttempts < @MaxAttempts))
                  AND (@Cutoff IS NULL OR ReceivedDate >= @Cutoff)
                ORDER BY ReceivedDate DESC, AttachmentName
            )
            UPDATE batch
                SET ProcessingStatus = 1,
                    ProcessingAttempts = ProcessingAttempts + 1,
                    ProcessingStartedAt = SYSUTCDATETIME(),
                    UpdatedAt = SYSUTCDATETIME()
            OUTPUT inserted.Id, inserted.SiteId, inserted.DriveId, inserted.GraphItemId,
                   inserted.AttachmentName, inserted.AttachmentMime;
            """;
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@BatchSize", SqlDbType.Int).Value = Math.Max(1, batchSize);
        command.Parameters.Add("@MaxAttempts", SqlDbType.Int).Value = Math.Max(1, maxAttempts);
        Add(command, "@Cutoff", SqlDbType.Date, null, cutoff?.ToDateTime(TimeOnly.MinValue));

        var claimed = new List<ProcessingCandidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            claimed.Add(new ProcessingCandidate(
                reader.GetGuid(0),
                NullableString(reader, "SiteId"),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                NullableString(reader, "AttachmentMime")));
        }
        return claimed;
    }

    public async Task MarkProcessedAsync(
        Guid id, ProcessingOutcome outcome, string? classifiedDocumentType, string? error, CancellationToken cancellationToken)
    {
        const string sql = """
            UPDATE dbo.CapturedDocument
                SET ProcessingStatus = @Status,
                    ClassifiedDocumentType = @ClassifiedType,
                    LastProcessingError = @Error,
                    LastProcessedAt = SYSUTCDATETIME(),
                    UpdatedAt = SYSUTCDATETIME()
            WHERE Id = @Id
            """;
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Id", SqlDbType.UniqueIdentifier).Value = id;
        command.Parameters.Add("@Status", SqlDbType.TinyInt).Value = (byte)outcome;
        Add(command, "@ClassifiedType", SqlDbType.NVarChar, 60, Clean(classifiedDocumentType));
        Add(command, "@Error", SqlDbType.NVarChar, -1, Clean(error));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private const string SelectColumns = """
        SELECT Id, SiteId, DriveId, GraphItemId, Subject, FromName, FromAddress, OriginalMailbox, ToAddresses, CcAddresses,
               MessageId, InternetMessageId, ConversationId, EmailBodyPreview, ReceivedAt, ReceivedDate, AttachmentName, AttachmentMime,
               HasExtraction, DetectedSupplier, ExtractedInvoiceNumber, ExtractedInvoiceDate, ExtractedReference, ExtractedTotal, ExtractedCurrency,
               ResolvedStatus, ResolvedDocumentType, SupplierMasterName, NwpSupplierOID, NwpSupplierReference, ResolvedInvoiceNumber, ResolvedInvoiceDate, ResolvedReference,
               ResolvedTotal, ResolvedCurrency, AssignedQueue, WorkflowOwner, ReviewerNote, ExceptionReason
        FROM dbo.CapturedDocument
        """;

    // Looked up by column name rather than ordinal position: SelectColumns is a wide,
    // hand-maintained column list and a positional mismatch here would silently scramble fields.
    private static CapturedDocument Map(SqlDataReader r)
    {
        var hasExtraction = r.GetBoolean(r.GetOrdinal("HasExtraction"));
        var resolvedStatus = NullableInt(r, "ResolvedStatus");
        var resolvedDocumentType = NullableInt(r, "ResolvedDocumentType");
        var receivedAtOrdinal = r.GetOrdinal("ReceivedAt");
        var subject = r.GetString(r.GetOrdinal("Subject"));
        var statementMismatch = hasExtraction && !resolvedStatus.HasValue
            && subject.Contains("statement", StringComparison.OrdinalIgnoreCase);

        return new CapturedDocument
        {
            Id = r.GetGuid(r.GetOrdinal("Id")),
            GraphSiteId = NullableString(r, "SiteId"),
            GraphDriveId = NullableString(r, "DriveId"),
            GraphItemId = NullableString(r, "GraphItemId"),
            Subject = subject,
            FromName = NullableString(r, "FromName"),
            FromAddress = NullableString(r, "FromAddress") ?? "",
            OriginalMailbox = r.GetString(r.GetOrdinal("OriginalMailbox")),
            ToAddresses = NullableString(r, "ToAddresses"),
            CcAddresses = NullableString(r, "CcAddresses"),
            MessageId = NullableString(r, "MessageId"),
            InternetMessageId = NullableString(r, "InternetMessageId"),
            ConversationId = NullableString(r, "ConversationId"),
            EmailBodyPreview = NullableString(r, "EmailBodyPreview"),
            ReceivedAt = r.IsDBNull(receivedAtOrdinal) ? null : r.GetDateTimeOffset(receivedAtOrdinal),
            ReceivedDate = DateOnly.FromDateTime(r.GetDateTime(r.GetOrdinal("ReceivedDate"))),
            AttachmentName = r.GetString(r.GetOrdinal("AttachmentName")),
            AttachmentMime = r.GetString(r.GetOrdinal("AttachmentMime")),
            DetectedSupplier = NullableString(r, "DetectedSupplier"),
            InvoiceNumber = NullableString(r, "ResolvedInvoiceNumber") ?? NullableString(r, "ExtractedInvoiceNumber"),
            InvoiceDate = NullableDate(r, "ResolvedInvoiceDate") ?? NullableDate(r, "ExtractedInvoiceDate"),
            BatchOrReference = NullableString(r, "ResolvedReference") ?? NullableString(r, "ExtractedReference"),
            Total = NullableDecimal(r, "ResolvedTotal") ?? NullableDecimal(r, "ExtractedTotal"),
            Currency = NullableString(r, "ResolvedCurrency") ?? NullableString(r, "ExtractedCurrency"),
            DocumentType = resolvedDocumentType.HasValue
                ? (DocumentType)resolvedDocumentType.Value
                : hasExtraction ? DocumentType.Invoice : DocumentType.Unknown,
            Status = resolvedStatus.HasValue
                ? (ClassificationStatus)resolvedStatus.Value
                : statementMismatch ? ClassificationStatus.Exception
                : hasExtraction ? ClassificationStatus.ReviewRequired : ClassificationStatus.Unclassified,
            ExceptionReason = NullableString(r, "ExceptionReason")
                ?? (statementMismatch ? "The subject identifies this as a statement, but extraction returned invoice fields. Review the original document before approving." : null),
            SupplierMasterName = NullableString(r, "SupplierMasterName"),
            NwpSupplierOid = NullableString(r, "NwpSupplierOID"),
            NwpSupplierReference = NullableString(r, "NwpSupplierReference"),
            AssignedQueue = NullableString(r, "AssignedQueue") ?? "Incoming",
            WorkflowOwner = NullableString(r, "WorkflowOwner"),
            ReviewerNote = NullableString(r, "ReviewerNote")
        };
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? NullableString(SqlDataReader r, string name) { var i = r.GetOrdinal(name); return r.IsDBNull(i) ? null : r.GetString(i); }
    private static int? NullableInt(SqlDataReader r, string name) { var i = r.GetOrdinal(name); return r.IsDBNull(i) ? null : r.GetInt32(i); }
    private static decimal? NullableDecimal(SqlDataReader r, string name) { var i = r.GetOrdinal(name); return r.IsDBNull(i) ? null : r.GetDecimal(i); }
    private static DateOnly? NullableDate(SqlDataReader r, string name) { var i = r.GetOrdinal(name); return r.IsDBNull(i) ? null : DateOnly.FromDateTime(r.GetDateTime(i)); }
    private static void Add(SqlCommand c, string name, SqlDbType type, int? size, object? value) { var p = size.HasValue ? c.Parameters.Add(name, type, size.Value) : c.Parameters.Add(name, type); p.Value = value ?? DBNull.Value; }
    private static void AddDecimal(SqlCommand c, string name, decimal? value, byte precision, byte scale) { var p = c.Parameters.Add(name, SqlDbType.Decimal); p.Precision = precision; p.Scale = scale; p.Value = value ?? (object)DBNull.Value; }
}
