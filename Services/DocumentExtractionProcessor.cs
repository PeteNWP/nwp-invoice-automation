using Nwp.InvoiceAutomation.Web.Models;

namespace Nwp.InvoiceAutomation.Web.Services;

/// <summary>Runs the shared Graph -> Copilot -> parse -> persist pipeline for one captured document.</summary>
public sealed class DocumentExtractionProcessor
{
    private const string Model = "CopilotStudio:NWP Invoice Extraction";
    private readonly IGraphDocumentClient _graph;
    private readonly ICopilotExtractionClient _copilot;
    private readonly IInvoiceExtractionRepository _extractions;
    private readonly ICapturedDocumentRepository _documents;

    public DocumentExtractionProcessor(
        IGraphDocumentClient graph,
        ICopilotExtractionClient copilot,
        IInvoiceExtractionRepository extractions,
        ICapturedDocumentRepository documents)
    {
        _graph = graph;
        _copilot = copilot;
        _extractions = extractions;
        _documents = documents;
    }

    public async Task<DocumentExtractionResult> ProcessAsync(
        ProcessingCandidate candidate, bool rejectApproved, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(candidate.SiteId)
            || string.IsNullOrWhiteSpace(candidate.DriveId)
            || string.IsNullOrWhiteSpace(candidate.GraphItemId))
            throw new InvalidOperationException("This document has no complete Microsoft Graph identity (SiteId, DriveId and ItemId are required). Refresh the SharePoint capture before retrying.");

        if (rejectApproved && await IsApprovedAsync(candidate, cancellationToken))
            throw new ApprovedExtractionException();

        byte[] bytes;
        string contentType;
        using (var content = await _graph.DownloadDriveItemAsync(
                   candidate.SiteId, candidate.DriveId, candidate.GraphItemId,
                   candidate.FileName, candidate.ContentType, cancellationToken))
        {
            using var buffer = new MemoryStream();
            await content.Stream.CopyToAsync(buffer, cancellationToken);
            bytes = buffer.ToArray();
            contentType = string.IsNullOrWhiteSpace(content.ContentType) ? "application/pdf" : content.ContentType;
        }

        var json = await _copilot.ExtractAsync(
            candidate.GraphItemId, candidate.FileName, contentType, bytes, cancellationToken);
        var parsed = CopilotExtractionResponse.TryParse(
            json, candidate.DriveId, candidate.GraphItemId, candidate.SiteId, Model);

        if (parsed is null)
        {
            const string error = "Copilot response could not be parsed as JSON.";
            await _documents.MarkProcessedAsync(candidate.Id, ProcessingOutcome.Failed, null, error, cancellationToken);
            return new DocumentExtractionResult(ProcessingOutcome.Failed, null, null, error);
        }

        if (!parsed.LooksLikeInvoice)
        {
            await _documents.MarkProcessedAsync(candidate.Id, ProcessingOutcome.Skipped, parsed.DocumentType, null, cancellationToken);
            return new DocumentExtractionResult(ProcessingOutcome.Skipped, parsed.DocumentType, null, null);
        }

        var saved = await _extractions.UpsertAsync(parsed.Extraction, cancellationToken);
        if (rejectApproved && !saved.Written)
            throw new ApprovedExtractionException();

        await _documents.MarkProcessedAsync(candidate.Id, ProcessingOutcome.Extracted, parsed.DocumentType, null, cancellationToken);
        return new DocumentExtractionResult(ProcessingOutcome.Extracted, parsed.DocumentType, parsed.Extraction, null);
    }

    private async Task<bool> IsApprovedAsync(ProcessingCandidate candidate, CancellationToken cancellationToken)
    {
        var matches = await _extractions.GetBatchAsync([(candidate.DriveId, candidate.GraphItemId)], cancellationToken);
        return matches.TryGetValue((candidate.DriveId, candidate.GraphItemId), out var extraction)
               && extraction.ReviewStatus == ExtractionReviewStatus.Approved;
    }
}

public sealed record DocumentExtractionResult(
    ProcessingOutcome Outcome, string? ClassifiedDocumentType, InvoiceExtractionRequest? Extraction, string? Error);

public sealed class ApprovedExtractionException : InvalidOperationException
{
    public ApprovedExtractionException()
        : base("This extraction has already been approved. Reprocessing was stopped to protect the approved human work.") { }
}
