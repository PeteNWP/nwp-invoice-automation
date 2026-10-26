using Nwp.InvoiceAutomation.Web.Models;

namespace Nwp.InvoiceAutomation.Web.Services;

/// <summary>
/// The UI's read path for captured documents. Reads SQL only — it never calls Microsoft
/// Graph. <see cref="GraphInboxSyncWorker"/> is the sole writer of capture/extraction data;
/// <see cref="ResolveExceptionAsync"/> is the only writer of human workflow decisions.
/// </summary>
public sealed class SqlCapturedDocumentStore : IDocumentStore
{
    private readonly ICapturedDocumentRepository _repository;
    private readonly MockDocumentStore _fallback;
    private readonly IConfiguration _configuration;
    private readonly IInvoiceExtractionRepository _extractions;
    private readonly ILogger<SqlCapturedDocumentStore> _logger;

    public SqlCapturedDocumentStore(
        ICapturedDocumentRepository repository,
        MockDocumentStore fallback,
        IConfiguration configuration,
        IInvoiceExtractionRepository extractions,
        ILogger<SqlCapturedDocumentStore> logger)
    {
        _repository = repository;
        _fallback = fallback;
        _configuration = configuration;
        _extractions = extractions;
        _logger = logger;
    }

    public string Source { get; private set; } = "SharePoint Incoming (synced to SQL)";

    public async Task<IReadOnlyList<CapturedDocument>> AllAsync(CancellationToken cancellationToken = default)
    {
        if (UseMockSource())
        {
            Source = _fallback.Source;
            return await _fallback.AllAsync(cancellationToken);
        }

        try
        {
            var documents = await _repository.GetAllAsync(cancellationToken);
            await AttachExtractionsAsync(documents, cancellationToken);
            Source = $"SharePoint: {_configuration["SharePointInbox:LibraryName"] ?? "Invoice Automation"}/{_configuration["SharePointInbox:FolderPath"]?.Trim('/') ?? "Incoming"} (synced to SQL)";
            return documents;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (_configuration.GetValue<bool>("SharePointInbox:FallbackToMock"))
            {
                _logger.LogWarning(ex, "SQL read of captured documents failed; falling back to mock/test records by configuration.");
                Source = "Mock/test records (SQL unreachable)";
                return await _fallback.AllAsync(cancellationToken);
            }

            _logger.LogError(ex, "SQL read of captured documents failed.");
            throw;
        }
    }

    public async Task<CapturedDocument?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (UseMockSource())
        {
            return await _fallback.GetAsync(id, cancellationToken);
        }

        var document = await _repository.GetAsync(id, cancellationToken);
        if (document is not null) await AttachExtractionsAsync([document], cancellationToken);
        return document;
    }

    public async Task<IReadOnlyList<CapturedDocument>> ByTypeAsync(DocumentType type, CancellationToken cancellationToken = default) =>
        (await AllAsync(cancellationToken)).Where(d => d.DocumentType == type).ToList();

    public async Task<IReadOnlyList<CapturedDocument>> ByStatusAsync(ClassificationStatus status, CancellationToken cancellationToken = default) =>
        (await AllAsync(cancellationToken)).Where(d => d.Status == status).ToList();

    public async Task ResolveExceptionAsync(Guid id, ExceptionResolution resolution, CancellationToken cancellationToken = default)
    {
        if (UseMockSource())
        {
            await _fallback.ResolveExceptionAsync(id, resolution, cancellationToken);
            return;
        }

        await _repository.ResolveExceptionAsync(id, resolution, cancellationToken);
    }

    private async Task AttachExtractionsAsync(IReadOnlyList<CapturedDocument> documents, CancellationToken cancellationToken)
    {
        var keys = documents.Where(d => d.GraphDriveId is not null && d.GraphItemId is not null)
            .Select(d => (d.GraphDriveId!, d.GraphItemId!));
        var extractions = await _extractions.GetBatchAsync(keys, cancellationToken);
        foreach (var document in documents)
        {
            if (document.GraphDriveId is null || document.GraphItemId is null ||
                !extractions.TryGetValue((document.GraphDriveId, document.GraphItemId), out var extraction)) continue;
            document.Extraction = extraction;
            // Observed extraction identity is deliberately not promoted to the authoritative master fields.
            document.DetectedSupplier = extraction.SupplierName;
            document.InvoiceNumber = extraction.InvoiceNumber;
            document.InvoiceDate = extraction.InvoiceDate;
            document.BatchOrReference = extraction.Reference;
            document.Total = extraction.Total;
            document.Currency = extraction.Currency;
            if (document.Status == ClassificationStatus.Unclassified)
            {
                document.DocumentType = DocumentType.Invoice;
                document.Status = ClassificationStatus.ReviewRequired;
            }
            // Older "Save for further review" posts resolved the captured document as
            // Classified even though the extraction remained unapproved. Normalise those
            // rows back into the active review queue; only explicit human approval is reviewed.
            if (document.Status == ClassificationStatus.Classified
                && extraction.ReviewStatus != ExtractionReviewStatus.Approved)
            {
                document.Status = ClassificationStatus.ReviewRequired;
            }
            if (document.Status == ClassificationStatus.ReviewRequired
                && document.Subject.Contains("statement", StringComparison.OrdinalIgnoreCase))
            {
                document.Status = ClassificationStatus.Exception;
                document.ExceptionReason = "The subject identifies this as a statement, but extraction returned invoice fields. Review the original document before approving.";
            }
        }
    }

    private bool UseMockSource() =>
        string.Equals(_configuration["DocumentSource"], "Mock", StringComparison.OrdinalIgnoreCase);
}
