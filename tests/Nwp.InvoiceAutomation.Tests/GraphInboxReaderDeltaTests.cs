using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nwp.InvoiceAutomation.Web.Models;
using Nwp.InvoiceAutomation.Web.Services;
using Xunit;

namespace Nwp.InvoiceAutomation.Tests;

/// <summary>
/// Exercises <see cref="GraphInboxReader.SyncDeltaAsync"/> against a scripted Microsoft Graph
/// over a fake <see cref="HttpMessageHandler"/>, so the delta-walk/filtering/retry/resync logic
/// is verified without a real tenant or SQL Server. The sync worker and repository that consume
/// this result are thin pass-through wiring and are covered by the build only.
/// </summary>
public sealed class GraphInboxReaderDeltaTests
{
    private const string DriveId = "drive-1";
    private const string SiteId = "site-1";
    private const string FolderItemId = "folder-1";
    private const string FolderLookupUrl = $"https://graph.microsoft.com/v1.0/sites/{SiteId}/drives/{DriveId}/root:/Incoming?$select=id";
    private const string InitialDeltaUrl = $"https://graph.microsoft.com/v1.0/drives/{DriveId}/items/{FolderItemId}/delta";

    [Fact]
    public async Task InitialSync_KeepsDirectChildrenOnly_AndReturnsFreshDeltaLink()
    {
        const string nextDeltaLink = "https://graph.microsoft.com/v1.0/drives/drive-1/items/folder-1/delta?token=cursor-1";
        var script = new ScriptedGraph()
            .WithToken()
            .WithSiteLookup(SiteId)
            .WithFolderLookup(FolderLookupUrl, FolderItemId)
            .WithJson(InitialDeltaUrl, HttpMethod.Get, $$"""
                {
                  "value": [
                    { "id": "kept-1", "name": "invoice1.pdf", "file": { "mimeType": "application/pdf" },
                      "parentReference": { "path": "/drives/{{DriveId}}/root:/Incoming" },
                      "createdDateTime": "2026-10-01T10:00:00Z" },
                    { "id": "dropped-1", "name": "sub-invoice.pdf", "file": { "mimeType": "application/pdf" },
                      "parentReference": { "path": "/drives/{{DriveId}}/root:/Incoming/Sub" },
                      "createdDateTime": "2026-10-01T10:00:00Z" },
                    { "id": "folder-skip", "name": "Sub", "folder": { "childCount": 1 },
                      "parentReference": { "path": "/drives/{{DriveId}}/root:/Incoming" } }
                  ],
                  "@odata.deltaLink": "{{nextDeltaLink}}"
                }
                """)
            .WithBatchMetadata(responseCount: 1);

        var reader = CreateReader(script.ToHandler());

        var result = await reader.SyncDeltaAsync(storedDeltaLink: null, CancellationToken.None);

        Assert.True(result.IsInitialSync);
        Assert.False(result.ResyncRequired);
        Assert.Equal(nextDeltaLink, result.DeltaLink);
        Assert.Equal(DriveId, result.DriveId);
        Assert.Empty(result.Deletions);
        var kept = Assert.Single(result.Upserts);
        Assert.Equal("kept-1", kept.GraphItemId);
    }

    [Fact]
    public async Task IncrementalSync_ReportsDeletions_AndDoesNotReconcile()
    {
        const string storedDeltaLink = "https://graph.microsoft.com/v1.0/drives/drive-1/items/folder-1/delta?token=cursor-old";
        const string nextDeltaLink = "https://graph.microsoft.com/v1.0/drives/drive-1/items/folder-1/delta?token=cursor-new";
        var script = new ScriptedGraph()
            .WithToken()
            .WithSiteLookup(SiteId)
            .WithJson(storedDeltaLink, HttpMethod.Get, $$"""
                {
                  "value": [
                    { "id": "removed-1", "deleted": { "state": "deleted" } }
                  ],
                  "@odata.deltaLink": "{{nextDeltaLink}}"
                }
                """);
        // No folder lookup and no $batch call are scripted: an incremental run with no new/changed
        // file items must not resolve the folder again or fetch metadata for anything.

        var reader = CreateReader(script.ToHandler());

        var result = await reader.SyncDeltaAsync(storedDeltaLink, CancellationToken.None);

        Assert.False(result.IsInitialSync);
        Assert.False(result.ResyncRequired);
        Assert.Equal(nextDeltaLink, result.DeltaLink);
        Assert.Empty(result.Upserts);
        var deletion = Assert.Single(result.Deletions);
        Assert.Equal((DriveId, "removed-1"), deletion);
    }

    [Fact]
    public async Task ExpiredCursor_Returns410_RebuildsFromFreshInitialDelta()
    {
        const string storedDeltaLink = "https://graph.microsoft.com/v1.0/drives/drive-1/items/folder-1/delta?token=expired";
        const string nextDeltaLink = "https://graph.microsoft.com/v1.0/drives/drive-1/items/folder-1/delta?token=rebuilt";
        var script = new ScriptedGraph()
            .WithToken()
            .WithSiteLookup(SiteId)
            .WithStatus(storedDeltaLink, HttpMethod.Get, HttpStatusCode.Gone, "{\"error\":{\"code\":\"resyncRequired\"}}")
            .WithFolderLookup(FolderLookupUrl, FolderItemId)
            .WithJson(InitialDeltaUrl, HttpMethod.Get, $$"""
                {
                  "value": [
                    { "id": "kept-1", "name": "invoice1.pdf", "file": { "mimeType": "application/pdf" },
                      "parentReference": { "path": "/drives/{{DriveId}}/root:/Incoming" },
                      "createdDateTime": "2026-10-01T10:00:00Z" }
                  ],
                  "@odata.deltaLink": "{{nextDeltaLink}}"
                }
                """)
            .WithBatchMetadata(responseCount: 1);

        var reader = CreateReader(script.ToHandler());

        var result = await reader.SyncDeltaAsync(storedDeltaLink, CancellationToken.None);

        Assert.True(result.ResyncRequired);
        Assert.True(result.IsInitialSync);
        Assert.Equal(nextDeltaLink, result.DeltaLink);
        Assert.Single(result.Upserts);
    }

    [Fact]
    public async Task Throttled429_RetriesBoundedly_ThenSucceeds()
    {
        const string nextDeltaLink = "https://graph.microsoft.com/v1.0/drives/drive-1/items/folder-1/delta?token=cursor-2";
        var script = new ScriptedGraph()
            .WithToken()
            .WithSiteLookup(SiteId)
            .WithFolderLookup(FolderLookupUrl, FolderItemId)
            .WithThrottledThenJson(InitialDeltaUrl, HttpMethod.Get, throttledAttempts: 3, $$"""
                {
                  "value": [
                    { "id": "kept-1", "name": "invoice1.pdf", "file": { "mimeType": "application/pdf" },
                      "parentReference": { "path": "/drives/{{DriveId}}/root:/Incoming" },
                      "createdDateTime": "2026-10-01T10:00:00Z" }
                  ],
                  "@odata.deltaLink": "{{nextDeltaLink}}"
                }
                """)
            .WithBatchMetadata(responseCount: 1);

        var reader = CreateReader(script.ToHandler());

        var result = await reader.SyncDeltaAsync(storedDeltaLink: null, CancellationToken.None);

        Assert.Single(result.Upserts);
        Assert.Equal(4, script.AttemptsFor(InitialDeltaUrl));
    }

    [Fact]
    public async Task PersistentThrottling_ExhaustsBoundedRetries_AndThrows()
    {
        var script = new ScriptedGraph()
            .WithToken()
            .WithSiteLookup(SiteId)
            .WithFolderLookup(FolderLookupUrl, FolderItemId)
            .WithThrottledThenJson(InitialDeltaUrl, HttpMethod.Get, throttledAttempts: int.MaxValue, "{}");

        var reader = CreateReader(script.ToHandler());

        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.SyncDeltaAsync(storedDeltaLink: null, CancellationToken.None));

        // SendWithRetryAsync caps at 4 attempts total (initial try + 3 retries).
        Assert.Equal(4, script.AttemptsFor(InitialDeltaUrl));
    }

    private static GraphInboxReader CreateReader(HttpMessageHandler handler)
    {
        var httpClientFactory = new FakeHttpClientFactory(handler);
        var graphOptions = Options.Create(new GraphDocumentOptions
        {
            Enabled = true,
            TenantId = "tenant-1",
            ClientId = "client-1",
            ClientSecret = "secret-1"
        });
        var siteOptions = Options.Create(new BenchmarkGraphOptions());
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SharePointInbox:FolderPath"] = "Incoming",
                ["SharePointInbox:SiteHostname"] = "contoso.sharepoint.com",
                ["SharePointInbox:DriveId"] = DriveId,
                ["Mailbox:PrimaryMailbox"] = "invoices@example.com"
            })
            .Build();

        return new GraphInboxReader(
            httpClientFactory,
            graphOptions,
            siteOptions,
            configuration,
            new FakeExtractionRepository(),
            NullLogger<GraphInboxReader>.Instance);
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        private readonly HttpClient _client = new(handler);
        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class FakeExtractionRepository : IInvoiceExtractionRepository
    {
        public Task<InvoiceExtractionResult> UpsertAsync(InvoiceExtractionRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not exercised by delta-sync tests.");

        public Task<IReadOnlyDictionary<(string DriveId, string DriveItemId), InvoiceExtraction>> GetBatchAsync(
            IEnumerable<(string DriveId, string DriveItemId)> keys, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<(string DriveId, string DriveItemId), InvoiceExtraction>>(
                new Dictionary<(string DriveId, string DriveItemId), InvoiceExtraction>());
    }

    /// <summary>
    /// A tiny scripted Graph double: register canned responses per (method, exact URL) and
    /// replay them, counting attempts so bounded-retry behaviour can be asserted directly.
    /// </summary>
    private sealed class ScriptedGraph
    {
        private sealed class Route
        {
            public required Func<int, HttpResponseMessage> Respond { get; init; }
            public int Attempts;
        }

        private readonly Dictionary<(HttpMethod Method, string Url), Route> _routes = new();

        public ScriptedGraph WithToken()
        {
            const string tokenUrl = "https://login.microsoftonline.com/tenant-1/oauth2/v2.0/token";
            return WithJson(tokenUrl, HttpMethod.Post, "{\"access_token\":\"fake-token\"}");
        }

        public ScriptedGraph WithSiteLookup(string siteId)
        {
            const string siteUrl = "https://graph.microsoft.com/v1.0/sites/contoso.sharepoint.com";
            return WithJson(siteUrl, HttpMethod.Get, $"{{\"id\":\"{siteId}\"}}");
        }

        public ScriptedGraph WithFolderLookup(string url, string folderItemId) =>
            WithJson(url, HttpMethod.Get, $"{{\"id\":\"{folderItemId}\"}}");

        public ScriptedGraph WithBatchMetadata(int responseCount)
        {
            var responses = string.Join(",", Enumerable.Range(0, responseCount)
                .Select(i => $"{{\"id\":\"{i}\",\"status\":200,\"body\":{{}}}}"));
            return WithJson("https://graph.microsoft.com/v1.0/$batch", HttpMethod.Post, $"{{\"responses\":[{responses}]}}");
        }

        public ScriptedGraph WithJson(string url, HttpMethod method, string json) =>
            WithStatus(url, method, HttpStatusCode.OK, json);

        public ScriptedGraph WithStatus(string url, HttpMethod method, HttpStatusCode status, string json)
        {
            _routes[(method, url)] = new Route { Respond = _ => MakeResponse(status, json) };
            return this;
        }

        /// <summary>Returns 429 (Retry-After: 0, so retries don't actually sleep) for the first
        /// <paramref name="throttledAttempts"/> attempts, then 200 with <paramref name="json"/>.</summary>
        public ScriptedGraph WithThrottledThenJson(string url, HttpMethod method, int throttledAttempts, string json)
        {
            _routes[(method, url)] = new Route
            {
                Respond = attempt => attempt <= throttledAttempts
                    ? MakeThrottledResponse()
                    : MakeResponse(HttpStatusCode.OK, json)
            };
            return this;
        }

        public int AttemptsFor(string url) =>
            _routes.TryGetValue((HttpMethod.Get, url), out var route) ? route.Attempts : 0;

        public HttpMessageHandler ToHandler() => new RoutingHandler(this);

        private HttpResponseMessage Respond(HttpRequestMessage request)
        {
            var url = request.RequestUri!.ToString();
            if (!_routes.TryGetValue((request.Method, url), out var route))
            {
                throw new InvalidOperationException($"Unscripted Graph request: {request.Method} {url}");
            }

            route.Attempts++;
            return route.Respond(route.Attempts);
        }

        private static HttpResponseMessage MakeResponse(HttpStatusCode status, string json) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        private static HttpResponseMessage MakeThrottledResponse()
        {
            var response = MakeResponse(HttpStatusCode.TooManyRequests, "{\"error\":{\"code\":\"TooManyRequests\"}}");
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
            return response;
        }

        private sealed class RoutingHandler(ScriptedGraph script) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(script.Respond(request));
        }
    }
}
