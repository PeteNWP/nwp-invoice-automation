namespace Nwp.InvoiceAutomation.Web.Models;

/// <summary>
/// Ground-truth expected values for a single captured document, established by human review.
/// This is the benchmark the AI/deterministic extraction is scored against — it is NOT the
/// live extraction result. Loaded from data/InvoiceBenchmarkHeaders.csv.
/// </summary>
public sealed class BenchmarkHeader
{
    public string SourceFilename { get; init; } = "";
    public string DocType { get; init; } = "";
    public string Supplier { get; init; } = "";
    public string InvoiceNumber { get; init; } = "";
    public string InvoiceDate { get; init; } = "";
    public string DueDate { get; init; } = "";
    public string Currency { get; init; } = "";
    public decimal? InvoiceTotal { get; init; }
    public string PORef { get; init; } = "";
    public int? ValidLineCount { get; init; }
    public decimal? ValidLineSum { get; init; }
    public string Reconciles { get; init; } = "";
    public string Notes { get; init; } = "";

    /// <summary>True when the document is a genuine invoice (not a customs/reject type).</summary>
    public bool IsInvoice => DocType.StartsWith("Invoice", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A single expected charge line for a benchmark invoice. LineAmount is kept as text because
/// some rows carry the "EXCLUDE-no-amount" flag rather than a number (e.g. Yukon's BT9 logger).
/// Loaded from data/InvoiceBenchmarkLines.csv.
/// </summary>
public sealed class BenchmarkLine
{
    public string SourceFilename { get; init; } = "";
    public string InvoiceNumber { get; init; } = "";
    public string LineNo { get; init; } = "";
    public string Description { get; init; } = "";
    public decimal? Quantity { get; init; }
    public decimal? UnitPrice { get; init; }
    public string LineAmount { get; init; } = "";

    /// <summary>True when this line has no payable amount and must be excluded from reconciliation.</summary>
    public bool IsExcluded =>
        LineAmount.Contains("EXCLUDE", StringComparison.OrdinalIgnoreCase)
        || !decimal.TryParse(LineAmount, out _);
}
