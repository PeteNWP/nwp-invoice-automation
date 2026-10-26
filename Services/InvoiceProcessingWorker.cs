using Microsoft.Extensions.Options;
using Nwp.InvoiceAutomation.Web.Models;

namespace Nwp.InvoiceAutomation.Web.Services;

/// <summary>
/// Drives captured documents through extraction. On a timer it claims a small batch of pending
/// documents from SQL, downloads each PDF from SharePoint via Graph, POSTs it to the Copilot
/// Studio <c>NWP Invoice Extraction</c> workflow, and writes the returned fields into
/// dbo.InvoiceExtraction / dbo.InvoiceExtractionLine through <see cref="IInvoiceExtractionRepository"/>.
///
/// It only ever writes the processing-state columns on dbo.CapturedDocument (owned exclusively
/// here) and the separate extraction tables — never the capture columns the Graph sync MERGE
/// owns — so the two background workers cannot fight.
/// </summary>
public sealed class InvoiceProcessingWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CopilotExtractionOptions _options;
    private readonly IConfiguration _configuration;
    private readonly ExtractionWorkerState _state;
    private readonly ILogger<InvoiceProcessingWorker> _logger;

    public InvoiceProcessingWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<CopilotExtractionOptions> options,
        IConfiguration configuration,
        ExtractionWorkerState state,
        ILogger<InvoiceProcessingWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _configuration = configuration;
        _state = state;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!IsEnabled())
        {
            _state.Enabled = false;
            _state.DisabledReason = "CopilotExtraction:Enabled is not true, GraphDocuments:Enabled is not true, or DocumentSource=Mock.";
            _logger.LogInformation("InvoiceProcessingWorker is disabled ({Reason}).", _state.DisabledReason);
            return;
        }
        if (string.IsNullOrWhiteSpace(_options.EndpointUrl))
        {
            _state.Enabled = false;
            _state.DisabledReason = "CopilotExtraction:EndpointUrl is not set.";
            _logger.LogWarning("InvoiceProcessingWorker is enabled but CopilotExtraction:EndpointUrl is not set; nothing will be processed.");
            return;
        }

        _state.Enabled = true;
        _logger.LogInformation("InvoiceProcessingWorker started (batch={Batch}, poll={Poll}s, cutoff={Cutoff}).",
            _options.BatchSize, _options.PollSeconds, _options.CutoffDate ?? "(none)");

        var interval = TimeSpan.FromSeconds(Math.Max(15, _options.PollSeconds));
        using var timer = new PeriodicTimer(interval);
        var cutoff = _options.ParseCutoff();

        do
        {
            try
            {
                await RunOnceAsync(cutoff, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _state.LastError = ex.Message;
                _logger.LogError(ex, "Invoice extraction cycle failed; will retry next interval.");
            }
        }
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunOnceAsync(DateOnly? cutoff, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ICapturedDocumentRepository>();
        var processor = scope.ServiceProvider.GetRequiredService<DocumentExtractionProcessor>();

        var batch = await repository.ClaimForProcessingAsync(_options.BatchSize, cutoff, _options.MaxAttempts, cancellationToken);
        _state.LastCycleAtUtc = DateTimeOffset.UtcNow;
        _state.LastCycleClaimed = batch.Count;
        if (batch.Count == 0) return;

        _logger.LogInformation("Extraction cycle claimed {Count} document(s).", batch.Count);

        foreach (var candidate in batch)
        {
            try
            {
                var result = await processor.ProcessAsync(candidate, rejectApproved: false, cancellationToken);
                if (result.Outcome == ProcessingOutcome.Failed)
                {
                    _state.TotalFailed++;
                    _state.LastError = $"Unparseable response for {candidate.FileName}.";
                    _logger.LogWarning("Unparseable extraction response for {File}.", candidate.FileName);
                }
                else if (result.Outcome == ProcessingOutcome.Skipped)
                {
                    _state.TotalSkipped++;
                    _logger.LogInformation("Skipped non-invoice {File} (documentType={Type}).", candidate.FileName, result.ClassifiedDocumentType);
                }
                else
                {
                    _state.TotalExtracted++;
                    _state.LastExtractedFile = candidate.FileName;
                    _logger.LogInformation(
                        "Extracted {File}: supplier={Supplier} invoice={Invoice} total={Total} lines={Lines}.",
                        candidate.FileName, result.Extraction?.SupplierName, result.Extraction?.InvoiceNumber,
                        result.Extraction?.Total, result.Extraction?.Lines.Count ?? 0);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Leave status Failed with the error; ClaimForProcessingAsync retries it until MaxAttempts.
                await repository.MarkProcessedAsync(candidate.Id, ProcessingOutcome.Failed, null, ex.Message, cancellationToken);
                _state.TotalFailed++;
                _state.LastError = $"{candidate.FileName}: {ex.Message}";
                _logger.LogWarning(ex, "Extraction failed for {File}; will retry until MaxAttempts.", candidate.FileName);
            }

            if (_options.PerDocumentDelayMs > 0)
            {
                try { await Task.Delay(_options.PerDocumentDelayMs, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            }
        }
    }

    private bool IsEnabled() =>
        _options.Enabled
        && !string.Equals(_configuration["DocumentSource"], "Mock", StringComparison.OrdinalIgnoreCase)
        && _configuration.GetValue<bool>("GraphDocuments:Enabled");
}
