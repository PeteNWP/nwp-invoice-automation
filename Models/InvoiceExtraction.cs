using System.ComponentModel.DataAnnotations;

namespace Nwp.InvoiceAutomation.Web.Models;

public sealed class InvoiceExtractionRequest
{
    [Required, StringLength(200)] public string DriveId { get; init; } = "";
    [Required, StringLength(200)] public string DriveItemId { get; init; } = "";
    [StringLength(200)] public string? SiteId { get; init; }
    [StringLength(300)] public string? SupplierName { get; init; }
    [StringLength(100)] public string? InvoiceNumber { get; init; }
    public DateOnly? InvoiceDate { get; init; }
    [StringLength(100)] public string? Reference { get; init; }
    [StringLength(3, MinimumLength = 3)] public string? Currency { get; init; }
    [Range(typeof(decimal), "-999999999999.9999", "999999999999.9999")] public decimal? Total { get; init; }
    [Range(0d, 1d)] public decimal? Confidence { get; init; }
    [StringLength(100)] public string? Model { get; init; }
    public DateTimeOffset? ExtractedAt { get; init; }
    [Required, MaxLength(500)] public List<InvoiceExtractionLineRequest> Lines { get; init; } = [];
}

public sealed class InvoiceExtractionLineRequest
{
    [Range(1, 100000)] public int LineNumber { get; init; }
    [StringLength(100)] public string? Sku { get; init; }
    [Required, StringLength(1000)] public string Description { get; init; } = "";
    [Range(typeof(decimal), "-999999999999.9999", "999999999999.9999")] public decimal? Quantity { get; init; }
    [Range(typeof(decimal), "-999999999999.9999", "999999999999.9999")] public decimal? UnitPrice { get; init; }
    [Range(typeof(decimal), "-999999999999.9999", "999999999999.9999")] public decimal? LineTotal { get; init; }
}

public sealed class InvoiceExtraction
{
    public long Id { get; init; }
    public string DriveId { get; init; } = "";
    public string DriveItemId { get; init; } = "";
    public string? SiteId { get; init; }
    public string? SupplierName { get; init; }
    public string? InvoiceNumber { get; init; }
    public DateOnly? InvoiceDate { get; init; }
    public string? Reference { get; init; }
    public string? Currency { get; init; }
    public decimal? Total { get; init; }
    public decimal? Confidence { get; init; }
    public string? Model { get; init; }
    public DateTimeOffset ExtractedAt { get; init; }
    public IReadOnlyList<InvoiceExtractionLine> Lines { get; init; } = [];
}

public sealed class InvoiceExtractionLine
{
    public int LineNumber { get; init; }
    public string? Sku { get; init; }
    public string Description { get; init; } = "";
    public decimal? Quantity { get; init; }
    public decimal? UnitPrice { get; init; }
    public decimal? LineTotal { get; init; }
}

public sealed record InvoiceExtractionResult(long Id, bool Created, int LineCount);
