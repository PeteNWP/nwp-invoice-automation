using Nwp.InvoiceAutomation.Web.Models;

namespace Nwp.InvoiceAutomation.Web.Services;

/// <summary>
/// Read access to captured mailbox documents. Backed by a mock in-memory store for the
/// shell app; a SQL-backed implementation will replace it once capture is real.
/// </summary>
public interface IDocumentStore
{
    string Source { get; }
    Task<IReadOnlyList<CapturedDocument>> AllAsync(CancellationToken cancellationToken = default);
    Task<CapturedDocument?> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CapturedDocument>> ByTypeAsync(DocumentType type, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CapturedDocument>> ByStatusAsync(ClassificationStatus status, CancellationToken cancellationToken = default);
    Task ResolveExceptionAsync(Guid id, ExceptionResolution resolution, CancellationToken cancellationToken = default);
}
