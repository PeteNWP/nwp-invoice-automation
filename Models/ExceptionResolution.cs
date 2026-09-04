namespace Nwp.InvoiceAutomation.Web.Models;

public sealed class ExceptionResolution
{
    public Guid DocumentId { get; init; }
    public DateTime ResolvedAtUtc { get; init; } = DateTime.UtcNow;
    public string ResolvedBy { get; init; } = "Demo user";
    public string SupplierMasterName { get; init; } = "";
    public DocumentType DocumentType { get; init; } = DocumentType.Invoice;
    public string? InvoiceNumber { get; init; }
    public DateOnly? InvoiceDate { get; init; }
    public string? BatchOrReference { get; init; }
    public decimal? Total { get; init; }
    public string Currency { get; init; } = "GBP";
    public string AssignedQueue { get; init; } = "Invoice Queue";
    public string? WorkflowOwner { get; init; }
    public bool TaughtSupplierAlias { get; init; }
    public bool TaughtNestedEmailRule { get; init; }
    public bool TaughtMailboxRoute { get; init; }
    public bool TaughtExtractionPattern { get; init; }
    public string? ResolutionNote { get; init; }
}
