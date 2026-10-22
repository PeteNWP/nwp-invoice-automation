namespace Nwp.InvoiceAutomation.Web.Services;

/// <summary>
/// Configuration for the Copilot Studio extraction worker. Bind from the "CopilotExtraction"
/// section of appsettings.Local.json (never Git — the endpoint URL carries a signed access key).
/// </summary>
public sealed class CopilotExtractionOptions
{
    /// <summary>Master switch. The worker does nothing unless this is true.</summary>
    public bool Enabled { get; set; }

    /// <summary>Full signed HTTP endpoint of the NWP Invoice Extraction workflow (includes its access key).</summary>
    public string? EndpointUrl { get; set; }

    /// <summary>Optional header-based key, for when the endpoint is secured away from the "Anyone" test setup.</summary>
    public string? ApiKeyHeaderName { get; set; }
    public string? ApiKey { get; set; }

    /// <summary>Documents claimed per cycle. Keep modest so Copilot/Graph are not hammered.</summary>
    public int BatchSize { get; set; } = 15;

    /// <summary>Seconds between cycles (floored at 15).</summary>
    public int PollSeconds { get; set; } = 120;

    /// <summary>Pause between individual documents within a cycle, to stay gentle on the workflow.</summary>
    public int PerDocumentDelayMs { get; set; } = 1500;

    /// <summary>A failed document is retried until it has been attempted this many times.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Per-call HTTP timeout. The workflow typically answers in ~30s.</summary>
    public int HttpTimeoutSeconds { get; set; } = 150;

    /// <summary>
    /// Only process documents received on/after this date (yyyy-MM-dd). Null = no floor.
    /// Used to start on the newest ~30 days before working backwards through the 1,391 backlog.
    /// </summary>
    public string? CutoffDate { get; set; }

    public DateOnly? ParseCutoff() =>
        DateOnly.TryParse(CutoffDate, out var d) ? d : null;
}
