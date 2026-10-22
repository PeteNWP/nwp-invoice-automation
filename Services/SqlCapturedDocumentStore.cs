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
    private readonly ILogger<SqlCapturedDocumentStore> _logger;

    public SqlCapturedDocumentStore(
        ICapturedDocumentRepository repository,
        MockDocumentStore fallback,
        IConfiguration configuration,
        ILogger<SqlCapturedDocumentStore> logger)
    {
        _repository = repository;
        _fallback = fallback;
        _configuration = configuration;
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

        return await _repository.GetAsync(id, cancellationToken);
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

    private bool UseMockSource() =>
        string.Equals(_configuration["DocumentSource"], "Mock", StringComparison.OrdinalIgnoreCase);
}
