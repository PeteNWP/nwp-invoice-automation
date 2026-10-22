using Nwp.InvoiceAutomation.Web.Models;

namespace Nwp.InvoiceAutomation.Web.Services;

/// <summary>
/// SQL-backed storage for captured Incoming-folder documents. The UI reads through this
/// repository only; Microsoft Graph is never called on a page request. The background
/// <see cref="GraphInboxSyncWorker"/> is the only writer of capture/extraction columns —
/// see <see cref="ApplyDeltaAsync"/>. Exception resolution (<see cref="ResolveExceptionAsync"/>)
/// is the only other writer and never touches capture/extraction columns, so a sync cycle
/// can never overwrite a human's decision.
/// </summary>
public interface ICapturedDocumentRepository
{
    /// <summary>The persisted Microsoft Graph delta cursor for <paramref name="syncKey"/>, or null when no sync has completed yet.</summary>
    Task<string?> GetDeltaLinkAsync(string syncKey, CancellationToken cancellationToken);

    /// <summary>
    /// Persists the next delta cursor. Called only after <see cref="ApplyDeltaAsync"/> has
    /// committed, so a crash in between just replays the same (idempotent) batch next run
    /// instead of silently losing it.
    /// </summary>
    Task SetDeltaLinkAsync(string syncKey, string? deltaLink, CancellationToken cancellationToken);

    /// <summary>
    /// Applies one delta batch transactionally: upserts changed/added documents and soft-deletes
    /// items Graph reported as removed. <paramref name="reconcileMissingAsDeleted"/> must only be
    /// true for an initial full delta pass, where <paramref name="upserts"/> is the complete
    /// current folder contents — never on an incremental run, where it would wrongly soft-delete
    /// everything that simply didn't change this cycle.
    /// </summary>
    Task ApplyDeltaAsync(
        string driveId,
        IReadOnlyList<CapturedDocument> upserts,
        IReadOnlyList<(string DriveId, string GraphItemId)> deletions,
        bool reconcileMissingAsDeleted,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CapturedDocument>> GetAllAsync(CancellationToken cancellationToken);
    Task<CapturedDocument?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task ResolveExceptionAsync(Guid id, ExceptionResolution resolution, CancellationToken cancellationToken);
    Task RecordSyncRunAsync(string syncKey, DateTimeOffset startedAt, bool succeeded, string? error, CancellationToken cancellationToken);
}
