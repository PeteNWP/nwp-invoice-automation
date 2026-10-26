using System.Collections.Concurrent;

namespace Nwp.InvoiceAutomation.Web.Services;

/// <summary>Explicit, one-document reprocessing entry point. It is independent of the background worker's Enabled flag.</summary>
public sealed class ManualInvoiceReprocessor
{
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Locks = new();
    private readonly ICapturedDocumentRepository _documents;
    private readonly DocumentExtractionProcessor _processor;

    public ManualInvoiceReprocessor(ICapturedDocumentRepository documents, DocumentExtractionProcessor processor)
    {
        _documents = documents;
        _processor = processor;
    }

    public async Task<DocumentExtractionResult> ReprocessAsync(Guid id, CancellationToken cancellationToken)
    {
        var gate = Locks.GetOrAdd(id, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var document = await _documents.GetAsync(id, cancellationToken)
                ?? throw new KeyNotFoundException("The selected captured document no longer exists.");

            var candidate = new ProcessingCandidate(
                document.Id,
                document.GraphSiteId,
                document.GraphDriveId ?? "",
                document.GraphItemId ?? "",
                document.AttachmentName,
                document.AttachmentMime);

            try
            {
                return await _processor.ProcessAsync(candidate, rejectApproved: true, cancellationToken);
            }
            catch (ApprovedExtractionException)
            {
                // Approval is a safety rejection, not a failed extraction attempt.
                throw;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                await _documents.MarkProcessedAsync(id, ProcessingOutcome.Failed, null, ex.Message, cancellationToken);
                throw;
            }
        }
        finally
        {
            gate.Release();
        }
    }
}
