namespace Nwp.InvoiceAutomation.Web.Services;

/// <summary>
/// Live, in-memory heartbeat for <see cref="InvoiceProcessingWorker"/>, surfaced by
/// <c>GET /api/extraction-status</c> so we can see at a glance whether the worker is enabled,
/// cycling, and what it last did — instead of inferring it from SQL. Resets on app restart.
/// </summary>
public sealed class ExtractionWorkerState
{
    public bool Enabled { get; set; }
    public string? DisabledReason { get; set; }
    public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastCycleAtUtc { get; set; }
    public int LastCycleClaimed { get; set; }
    public long TotalExtracted { get; set; }
    public long TotalSkipped { get; set; }
    public long TotalFailed { get; set; }
    public string? LastError { get; set; }
    public string? LastExtractedFile { get; set; }
}
