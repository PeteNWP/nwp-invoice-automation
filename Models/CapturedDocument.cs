namespace Nwp.InvoiceAutomation.Web.Models;

/// <summary>
/// A single item captured from the invoice mailbox: an email plus its attachment(s).
/// This is intentionally a flat, mock-friendly shape for the shell app. It will be
/// split into CapturedEmail / StoredDocument / InvoiceHeader as real parsing lands.
/// </summary>
public sealed class CapturedDocument
{
    public Guid Id { get; init; } = Guid.NewGuid();

    // --- Email envelope ---
    public string Subject { get; init; } = "";
    public string FromAddress { get; init; } = "";
    public string OriginalMailbox { get; init; } = "";
    public string? ForwardedBy { get; init; }
    public DateOnly ReceivedDate { get; init; }

    // --- Attachment ---
    public string AttachmentName { get; init; } = "";
    public string AttachmentMime { get; init; } = "";
    public bool IsNestedEmail { get; init; }

    // --- Classification ---
    public DocumentType DocumentType { get; set; } = DocumentType.Unknown;
    public ClassificationStatus Status { get; set; } = ClassificationStatus.Unclassified;
    public string? ExceptionReason { get; set; }

    // --- Supplier / invoice metadata (best-effort at capture time) ---
    public string? DetectedSupplier { get; init; }
    public string? SupplierMasterName { get; init; }
    public string? InvoiceNumber { get; init; }
    public DateOnly? InvoiceDate { get; init; }
    public string? BatchOrReference { get; init; }
    public decimal? Total { get; init; }
    public string? Currency { get; init; }

    // --- Workflow routing ---
    public string? AssignedQueue { get; init; }
    public string? WorkflowOwner { get; init; }

    /// <summary>Maeve's note from the forwarded email — high-value training label.</summary>
    public string? ReviewerNote { get; init; }

    public bool LateReceivedFlag =>
        InvoiceDate is { } inv && (ReceivedDate.DayNumber - inv.DayNumber) > 14;
}
