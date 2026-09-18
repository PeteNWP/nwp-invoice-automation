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

    /// <summary>Optional local/static URL for previewing the original document in the prototype.</summary>
    public string? AttachmentPreviewUrl { get; init; }

    // Stable Graph identity for documents loaded from the SharePoint Incoming folder.
    public string? GraphSiteId { get; init; }
    public string? GraphDriveId { get; init; }
    public string? GraphItemId { get; init; }

    // --- Classification ---
    public DocumentType DocumentType { get; set; } = DocumentType.Unknown;
    public ClassificationStatus Status { get; set; } = ClassificationStatus.Unclassified;
    public string? ExceptionReason { get; set; }

    // --- Supplier / invoice metadata (best-effort at capture time) ---
    public string? DetectedSupplier { get; set; }
    public string? SupplierMasterName { get; set; }
    public string? InvoiceNumber { get; set; }
    public DateOnly? InvoiceDate { get; set; }
    public string? BatchOrReference { get; set; }
    public decimal? Total { get; set; }
    public string? Currency { get; set; }

    // --- Workflow routing ---
    public string? AssignedQueue { get; set; }
    public string? WorkflowOwner { get; set; }

    /// <summary>Maeve's note from the forwarded email — high-value training label.</summary>
    public string? ReviewerNote { get; set; }

    public bool LateReceivedFlag =>
        InvoiceDate is { } inv && (ReceivedDate.DayNumber - inv.DayNumber) > 14;
}
