using Nwp.InvoiceAutomation.Web.Models;

namespace Nwp.InvoiceAutomation.Web.Services;

/// <summary>
/// Read access to captured mailbox documents. Backed by a mock in-memory store for the
/// shell app; a SQL-backed implementation will replace it once capture is real.
/// </summary>
public interface IDocumentStore
{
    IReadOnlyList<CapturedDocument> All();
    CapturedDocument? Get(Guid id);
    IReadOnlyList<CapturedDocument> ByType(DocumentType type);
    IReadOnlyList<CapturedDocument> ByStatus(ClassificationStatus status);
    void ResolveException(Guid id, ExceptionResolution resolution);
}
