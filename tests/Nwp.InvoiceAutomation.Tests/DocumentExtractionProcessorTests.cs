using System.Text;
using Nwp.InvoiceAutomation.Web.Models;
using Nwp.InvoiceAutomation.Web.Services;
using Xunit;

namespace Nwp.InvoiceAutomation.Tests;

public sealed class DocumentExtractionProcessorTests
{
    private static ProcessingCandidate Candidate(string? site = "site-1", string drive = "drive-1", string item = "item-1") =>
        new(Guid.NewGuid(), site, drive, item, "wealmoor.pdf", "application/pdf");

    [Fact]
    public async Task ProcessAsync_RejectsMissingGraphIdentity()
    {
        var processor = Build(out _, out _, out _);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            processor.ProcessAsync(Candidate(site: "", drive: "", item: ""), rejectApproved: true, default));
    }

    [Fact]
    public async Task ProcessAsync_PersistsExtractionWithRawResponse_OnInvoice()
    {
        const string raw = "{\"invoiceNumber\":\"INV-100\",\"supplierName\":\"Wealmoor\",\"totalAmount\":42.50}";
        var processor = Build(out var extractions, out var documents, out _, copilotJson: raw);
        var candidate = Candidate();

        var result = await processor.ProcessAsync(candidate, rejectApproved: true, default);

        Assert.Equal(ProcessingOutcome.Extracted, result.Outcome);
        Assert.Equal(raw, extractions.LastUpsert?.RawResponse);
        Assert.Equal("INV-100", result.Extraction?.InvoiceNumber);
        Assert.Equal((candidate.Id, ProcessingOutcome.Extracted), documents.LastMark);
    }

    [Fact]
    public async Task ProcessAsync_SkipsNonInvoice()
    {
        var processor = Build(out var extractions, out var documents, out _,
            copilotJson: "{\"documentType\":\"statement\"}");

        var result = await processor.ProcessAsync(Candidate(), rejectApproved: true, default);

        Assert.Equal(ProcessingOutcome.Skipped, result.Outcome);
        Assert.Null(extractions.LastUpsert);
        Assert.Equal(ProcessingOutcome.Skipped, documents.LastMark?.Outcome);
    }

    [Fact]
    public async Task ProcessAsync_FailsOnUnparseableResponse()
    {
        var processor = Build(out var extractions, out var documents, out _, copilotJson: "not json");

        var result = await processor.ProcessAsync(Candidate(), rejectApproved: true, default);

        Assert.Equal(ProcessingOutcome.Failed, result.Outcome);
        Assert.Null(extractions.LastUpsert);
        Assert.Equal(ProcessingOutcome.Failed, documents.LastMark?.Outcome);
    }

    [Fact]
    public async Task ProcessAsync_RejectsApprovedExtraction_WithoutDownloading()
    {
        var candidate = Candidate();
        var processor = Build(out _, out _, out var graph,
            copilotJson: "{\"invoiceNumber\":\"INV-1\"}",
            approved: (candidate.DriveId, candidate.GraphItemId));

        await Assert.ThrowsAsync<ApprovedExtractionException>(() =>
            processor.ProcessAsync(candidate, rejectApproved: true, default));
        Assert.Equal(0, graph.Downloads);
    }

    private static DocumentExtractionProcessor Build(
        out FakeExtractionRepository extractions,
        out FakeDocumentRepository documents,
        out FakeGraphClient graph,
        string copilotJson = "{}",
        (string DriveId, string DriveItemId)? approved = null)
    {
        extractions = new FakeExtractionRepository(approved);
        documents = new FakeDocumentRepository();
        graph = new FakeGraphClient();
        return new DocumentExtractionProcessor(graph, new FakeCopilotClient(copilotJson), extractions, documents);
    }

    private sealed class FakeGraphClient : IGraphDocumentClient
    {
        public int Downloads { get; private set; }

        public Task<GraphDocumentContent> DownloadFromSharingUrlAsync(string sharingUrl, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<GraphDocumentContent> DownloadDriveItemAsync(
            string siteId, string driveId, string itemId, string fileName, string? contentType = null, CancellationToken cancellationToken = default)
        {
            Downloads++;
            var stream = new MemoryStream(Encoding.UTF8.GetBytes("%PDF-1.4 fake"));
            return Task.FromResult(new GraphDocumentContent(stream, contentType ?? "application/pdf", fileName));
        }
    }

    private sealed class FakeCopilotClient(string json) : ICopilotExtractionClient
    {
        public Task<string> ExtractAsync(string documentId, string fileName, string contentType, byte[] content, CancellationToken cancellationToken)
            => Task.FromResult(json);
    }

    private sealed class FakeExtractionRepository(
        (string DriveId, string DriveItemId)? approved) : IInvoiceExtractionRepository
    {
        public InvoiceExtractionRequest? LastUpsert { get; private set; }

        public Task<InvoiceExtractionResult> UpsertAsync(InvoiceExtractionRequest request, CancellationToken cancellationToken)
        {
            LastUpsert = request;
            var blocked = approved is { } key && key.DriveId == request.DriveId && key.DriveItemId == request.DriveItemId;
            return Task.FromResult(new InvoiceExtractionResult(1, Created: true, request.Lines.Count, Written: !blocked));
        }

        public Task<IReadOnlyDictionary<(string DriveId, string DriveItemId), InvoiceExtraction>> GetBatchAsync(
            IEnumerable<(string DriveId, string DriveItemId)> keys, CancellationToken cancellationToken)
        {
            var map = new Dictionary<(string, string), InvoiceExtraction>();
            if (approved is { } key)
                map[key] = new InvoiceExtraction { DriveId = key.DriveId, DriveItemId = key.DriveItemId, ReviewStatus = ExtractionReviewStatus.Approved };
            return Task.FromResult((IReadOnlyDictionary<(string, string), InvoiceExtraction>)map);
        }

        public Task SaveReviewAsync(InvoiceExtractionReview review, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeDocumentRepository : ICapturedDocumentRepository
    {
        public (Guid Id, ProcessingOutcome Outcome)? LastMark { get; private set; }

        public Task MarkProcessedAsync(Guid id, ProcessingOutcome outcome, string? classifiedDocumentType, string? error, CancellationToken cancellationToken)
        {
            LastMark = (id, outcome);
            return Task.CompletedTask;
        }

        public Task<string?> GetDeltaLinkAsync(string syncKey, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public Task SetDeltaLinkAsync(string syncKey, string? deltaLink, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ApplyDeltaAsync(string driveId, IReadOnlyList<CapturedDocument> upserts, IReadOnlyList<(string DriveId, string GraphItemId)> deletions, bool reconcileMissingAsDeleted, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<CapturedDocument>> GetAllAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<CapturedDocument>>([]);
        public Task<CapturedDocument?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<CapturedDocument?>(null);
        public Task<int> ResolveExceptionAsync(Guid id, ExceptionResolution resolution, CancellationToken cancellationToken) => Task.FromResult(0);
        public Task RecordSyncRunAsync(string syncKey, DateTimeOffset startedAt, bool succeeded, string? error, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<ProcessingCandidate>> ClaimForProcessingAsync(int batchSize, DateOnly? cutoff, int maxAttempts, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ProcessingCandidate>>([]);
    }
}
