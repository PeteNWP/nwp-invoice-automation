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
    private readonly ILogger<InvoiceProcessingWorker> _logger;

    public InvoiceProcessingWorker(
        IServiceScopeFactory scopeFactory,
        IOptions<CopilotExtractionOptions> options,
        IConfiguration configuration,
        ILogger<InvoiceProcessingWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!IsEnabled())
        {
            _logger.LogInformation("InvoiceProcessingWorker is disabled (CopilotExtraction:Enabled is not true, GraphDocuments:Enabled is not true, or DocumentSource=Mock).");
            return;
        }
        if (string.IsNullOrWhiteSpace(_options.EndpointUrl))
        {
            _logger.LogWarning("InvoiceProcessingWorker is enabled but CopilotExtraction:EndpointUrl is not set; nothing will be processed.");
            return;
        }

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
                _logger.LogError(ex, "Invoice extraction cycle failed; will retry next interval.");
            }
        }
        while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunOnceAsync(DateOnly? cutoff, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ICapturedDocumentRepository>();
        var graph = scope.ServiceProvider.GetRequiredService<IGraphDocumentClient>();
        var copilot = scope.ServiceProvider.GetRequiredService<ICopilotExtractionClient>();
        var extractions = scope.ServiceProvider.GetRequiredService<IInvoiceExtractionRepository>();

        var batch = await repository.ClaimForProcessingAsync(_options.BatchSize, cutoff, _options.MaxAttempts, cancellationToken);
        if (batch.Count == 0) return;

        _logger.LogInformation("Extraction cycle claimed {Count} document(s).", batch.Count);
        const string model = "CopilotStudio:NWP Invoice Extraction";

        foreach (var candidate in batch)
        {
            try
            {
                byte[] bytes;
                string contentType;
                using (var content = await graph.DownloadDriveItemAsync(
                           candidate.SiteId ?? "", candidate.DriveId, candidate.GraphItemId,
                           candidate.FileName, candidate.ContentType, cancellationToken))
                {
                    using var buffer = new MemoryStream();
                    await content.Stream.CopyToAsync(buffer, cancellationToken);
                    bytes = buffer.ToArray();
                    contentType = string.IsNullOrWhiteSpace(content.ContentType) ? "application/pdf" : content.ContentType;
                }

                var json = await copilot.ExtractAsync(
                    candidate.GraphItemId, candidate.FileName, contentType, bytes, cancellationToken);

                var parsed = CopilotExtractionResponse.TryParse(
                    json, candidate.DriveId, candidate.GraphItemId, candidate.SiteId, model);

                if (parsed is null)
                {
                    await repository.MarkProcessedAsync(candidate.Id, ProcessingOutcome.Failed, null,
                        "Copilot response could not be parsed as JSON.", cancellationToken);
                    _logger.LogWarning("Unparseable extraction response for {File}.", candidate.FileName);
                }
                else if (!parsed.LooksLikeInvoice)
                {
                    // A valid response that isn't an invoice (statement, ENS, acknowledgement, "other").
                    // Record the classification; don't pollute the extraction tables.
                    await repository.MarkProcessedAsync(candidate.Id, ProcessingOutcome.Skipped, parsed.DocumentType,
                        null, cancellationToken);
                    _logger.LogInformation("Skipped non-invoice {File} (documentType={Type}).", candidate.FileName, parsed.DocumentType);
                }
                else
                {
                    await extractions.UpsertAsync(parsed.Extraction, cancellationToken);
                    await repository.MarkProcessedAsync(candidate.Id, ProcessingOutcome.Extracted, parsed.DocumentType,
                        null, cancellationToken);
                    _logger.LogInformation(
                        "Extracted {File}: supplier={Supplier} invoice={Invoice} total={Total} lines={Lines}.",
                        candidate.FileName, parsed.Extraction.SupplierName, parsed.Extraction.InvoiceNumber,
                        parsed.Extraction.Total, parsed.Extraction.Lines.Count);
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
