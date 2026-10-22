namespace Nwp.InvoiceAutomation.Web.Services;

/// <summary>
/// Pulls the SharePoint Incoming folder on a timer and writes the result to SQL. This is
/// the only thing in the app that calls Microsoft Graph for inbox capture; every UI page
/// reads <see cref="ICapturedDocumentRepository"/> (SQL) and never waits on Graph.
/// </summary>
public sealed class GraphInboxSyncWorker : BackgroundService
{
    private const string SyncKey = "SharePointIncoming";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GraphInboxSyncWorker> _logger;

    public GraphInboxSyncWorker(IServiceScopeFactory scopeFactory, IConfiguration configuration, ILogger<GraphInboxSyncWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!IsEnabled())
        {
            _logger.LogInformation("GraphInboxSyncWorker is disabled (DocumentSource is not SharePoint or GraphDocuments:Enabled is not true).");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(30, _configuration.GetValue("SharePointInbox:SyncIntervalSeconds", 300)));
        using var timer = new PeriodicTimer(interval);

        do
        {
            await RunOnceAsync(stoppingToken);
        }
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        using var scope = _scopeFactory.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<GraphInboxReader>();
        var repository = scope.ServiceProvider.GetRequiredService<ICapturedDocumentRepository>();

        try
        {
            var storedDeltaLink = await repository.GetDeltaLinkAsync(SyncKey, cancellationToken);
            var result = await reader.SyncDeltaAsync(storedDeltaLink, cancellationToken);

            // Commit the document changes before moving the cursor: if the process dies between
            // these two calls, the old (still-stored) cursor just replays this same idempotent
            // batch next run instead of silently losing it.
            await repository.ApplyDeltaAsync(
                result.DriveId,
                result.Upserts,
                result.Deletions,
                reconcileMissingAsDeleted: result.IsInitialSync,
                cancellationToken);
            await repository.SetDeltaLinkAsync(SyncKey, result.DeltaLink, cancellationToken);

            await repository.RecordSyncRunAsync(SyncKey, startedAt, succeeded: true, error: null, cancellationToken);

            if (result.ResyncRequired)
            {
                _logger.LogWarning(
                    "SharePoint Incoming delta cursor had expired (410); rebuilt from a fresh full delta with {Count} document(s).",
                    result.Upserts.Count);
            }

            _logger.LogInformation(
                "Synced SharePoint Incoming to SQL: {UpsertCount} changed document(s), {DeletionCount} removal(s) ({Mode}).",
                result.Upserts.Count, result.Deletions.Count, result.IsInitialSync ? "initial delta" : "incremental delta");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutting down; nothing to record.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "SharePoint Incoming sync failed; SQL keeps the last successfully synced state.");
            try
            {
                await repository.RecordSyncRunAsync(SyncKey, startedAt, succeeded: false, error: ex.Message, cancellationToken);
            }
            catch (Exception recordEx)
            {
                _logger.LogWarning(recordEx, "Could not record the failed sync run in dbo.GraphSyncState.");
            }
        }
    }

    private bool IsEnabled() =>
        !string.Equals(_configuration["DocumentSource"], "Mock", StringComparison.OrdinalIgnoreCase)
        && _configuration.GetValue<bool>("GraphDocuments:Enabled");
}
