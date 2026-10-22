using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Nwp.InvoiceAutomation.Web.Models;

namespace Nwp.InvoiceAutomation.Web.Services;

/// <summary>
/// Reads the SharePoint Invoice Automation/Incoming folder straight from Microsoft Graph.
/// This is the only place in the app that calls Graph for inbox capture, and it is only
/// ever called by <see cref="GraphInboxSyncWorker"/> on its own schedule — never from a
/// UI request. The worker persists the result to SQL; <see cref="SqlCapturedDocumentStore"/>
/// is what the UI actually reads.
///
/// <see cref="SyncDeltaAsync"/> drives a Microsoft Graph delta query
/// (/drives/{id}/items/{folderItemId}/delta) instead of a full folder listing. With no
/// stored cursor it walks the complete initial delta (every current item in the folder);
/// with a stored cursor it follows that link and only sees items that actually changed.
/// Graph's item-scoped delta recurses into subfolders, so results are filtered client-side
/// to direct children of the configured Incoming folder to match the previous non-recursive
/// :/children listing. See docs/SharePointEmailMetadata.md.
/// </summary>
/// <summary>Outcome of one <see cref="GraphInboxReader.SyncDeltaAsync"/> call.</summary>
/// <param name="DriveId">The Incoming folder's drive id.</param>
/// <param name="Upserts">Changed/added file items, enriched with email metadata and extraction data. On an initial sync this is the complete current folder contents.</param>
/// <param name="Deletions">(DriveId, GraphItemId) pairs Graph reported as removed.</param>
/// <param name="DeltaLink">The new cursor to persist via <see cref="ICapturedDocumentRepository.SetDeltaLinkAsync"/> once <see cref="Upserts"/>/<see cref="Deletions"/> are committed.</param>
/// <param name="IsInitialSync">True when this was a full initial delta pass (no prior cursor, or a 410-triggered rebuild).</param>
/// <param name="ResyncRequired">True when Graph returned 410 (resyncRequired) and this result is from the rebuilt pass.</param>
public sealed record GraphDeltaResult(
    string DriveId,
    IReadOnlyList<CapturedDocument> Upserts,
    IReadOnlyList<(string DriveId, string GraphItemId)> Deletions,
    string? DeltaLink,
    bool IsInitialSync,
    bool ResyncRequired);

public sealed class GraphInboxReader
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly GraphDocumentOptions _graphOptions;
    private readonly BenchmarkGraphOptions _siteOptions;
    private readonly IConfiguration _configuration;
    private readonly IInvoiceExtractionRepository _extractions;
    private readonly ILogger<GraphInboxReader> _logger;

    public GraphInboxReader(
        IHttpClientFactory httpClientFactory,
        IOptions<GraphDocumentOptions> graphOptions,
        IOptions<BenchmarkGraphOptions> siteOptions,
        IConfiguration configuration,
        IInvoiceExtractionRepository extractions,
        ILogger<GraphInboxReader> logger)
    {
        _httpClientFactory = httpClientFactory;
        _graphOptions = graphOptions.Value;
        _siteOptions = siteOptions.Value;
        _configuration = configuration;
        _extractions = extractions;
        _logger = logger;
    }

    private HttpClient Http => _httpClientFactory.CreateClient("sharepoint-inbox");

    public string FolderPath() =>
        _configuration["SharePointInbox:FolderPath"]?.Trim('/')
        ?? "Incoming";

    /// <summary>
    /// Runs one Graph delta cycle against the Incoming folder. With <paramref name="storedDeltaLink"/>
    /// null/blank this performs the initial full delta pass (every current item comes back as an
    /// "addition" since there is no prior cursor); otherwise it follows the stored cursor and only
    /// sees items that changed since the last run. A 410 (resyncRequired) response is handled by
    /// clearing the cursor and transparently rebuilding from a fresh initial pass within this same
    /// call, so the caller always gets a usable result and a valid new delta link.
    /// </summary>
    public async Task<GraphDeltaResult> SyncDeltaAsync(string? storedDeltaLink, CancellationToken cancellationToken)
    {
        if (!_graphOptions.Enabled)
        {
            throw new InvalidOperationException("DocumentSource is SharePoint but GraphDocuments:Enabled is not true.");
        }

        var token = await GetAccessTokenAsync(cancellationToken);
        var siteId = await ResolveSiteIdAsync(token, cancellationToken);
        var driveId = await ResolveDriveIdAsync(token, siteId, cancellationToken);

        var isInitialSync = string.IsNullOrWhiteSpace(storedDeltaLink);
        var startUrl = isInitialSync
            ? await BuildInitialDeltaUrlAsync(token, siteId, driveId, cancellationToken)
            : storedDeltaLink!;

        try
        {
            return await WalkDeltaAsync(token, siteId, driveId, startUrl, isInitialSync, resyncRequired: false, cancellationToken);
        }
        catch (GraphDeltaResyncRequiredException)
        {
            _logger.LogWarning(
                "SharePoint Incoming delta cursor returned 410 (resyncRequired); clearing the cursor and rebuilding from a fresh full delta.");
            var freshUrl = await BuildInitialDeltaUrlAsync(token, siteId, driveId, cancellationToken);
            return await WalkDeltaAsync(token, siteId, driveId, freshUrl, isInitialSync: true, resyncRequired: true, cancellationToken);
        }
    }

    private async Task<string> BuildInitialDeltaUrlAsync(string token, string siteId, string driveId, CancellationToken cancellationToken)
    {
        var folderItemId = await ResolveFolderItemIdAsync(token, siteId, driveId, cancellationToken);
        return $"https://graph.microsoft.com/v1.0/drives/{driveId}/items/{folderItemId}/delta";
    }

    private async Task<string> ResolveFolderItemIdAsync(string token, string siteId, string driveId, CancellationToken cancellationToken)
    {
        var encodedPath = string.Join("/", FolderPath().Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        var url = $"https://graph.microsoft.com/v1.0/sites/{siteId}/drives/{driveId}/root:/{encodedPath}?$select=id";
        using var response = await SendWithRetryAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return request;
        }, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Graph Incoming-folder lookup failed: {(int)response.StatusCode} {response.ReasonPhrase}. {json}");
        }

        using var payload = JsonDocument.Parse(json);
        return payload.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Graph Incoming-folder lookup did not return an id.");
    }

    /// <summary>
    /// Pages through one delta walk (initial or incremental) to completion, splitting items into
    /// upserts and deletions. Deliberately does not $select fields on the delta request: Graph can
    /// omit the `deleted` facet for deleted items when $select is applied, which would make
    /// deletions invisible.
    /// </summary>
    private async Task<GraphDeltaResult> WalkDeltaAsync(
        string token, string siteId, string driveId, string startUrl, bool isInitialSync, bool resyncRequired, CancellationToken cancellationToken)
    {
        var upserts = new List<CapturedDocument>();
        var deletions = new List<(string DriveId, string GraphItemId)>();
        string? url = startUrl;
        string? finalDeltaLink = null;

        while (!string.IsNullOrWhiteSpace(url))
        {
            var currentUrl = url;
            using var response = await SendWithRetryAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Get, currentUrl);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                return request;
            }, cancellationToken);

            if (response.StatusCode == HttpStatusCode.Gone)
            {
                throw new GraphDeltaResyncRequiredException();
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Graph Incoming-folder delta read failed: {(int)response.StatusCode} {response.ReasonPhrase}. {json}");
            }

            using var payload = JsonDocument.Parse(json);
            var root = payload.RootElement;
            if (root.TryGetProperty("value", out var items))
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (item.TryGetProperty("deleted", out _))
                    {
                        var deletedId = item.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(deletedId))
                        {
                            deletions.Add((driveId, deletedId));
                        }

                        continue;
                    }

                    if (!item.TryGetProperty("file", out var file))
                    {
                        continue; // folder or package item; Incoming only tracks files.
                    }

                    if (!IsDirectChildOfConfiguredFolder(item))
                    {
                        continue; // item-scoped delta recurses into subfolders; keep this non-recursive.
                    }

                    var itemId = item.GetProperty("id").GetString() ?? "";
                    var name = item.GetProperty("name").GetString() ?? "Unnamed document";
                    var mimeType = file.TryGetProperty("mimeType", out var mime) ? mime.GetString() ?? "application/octet-stream" : "application/octet-stream";
                    var received = ReadDate(item, "createdDateTime") ?? ReadDate(item, "lastModifiedDateTime") ?? DateTimeOffset.UtcNow;

                    upserts.Add(new CapturedDocument
                    {
                        Id = StableGuid(itemId),
                        Subject = name,
                        OriginalMailbox = _configuration["Mailbox:PrimaryMailbox"] ?? "invoices-evesham@nationwideproduce.com",
                        ReceivedAt = received,
                        ReceivedDate = DateOnly.FromDateTime(received.UtcDateTime),
                        AttachmentName = name,
                        AttachmentMime = mimeType,
                        GraphSiteId = siteId,
                        GraphDriveId = driveId,
                        GraphItemId = itemId,
                        DocumentType = DocumentType.Unknown,
                        Status = ClassificationStatus.Unclassified,
                        AssignedQueue = "Incoming"
                    });
                }
            }

            if (root.TryGetProperty("@odata.nextLink", out var next))
            {
                url = next.GetString();
            }
            else
            {
                finalDeltaLink = root.TryGetProperty("@odata.deltaLink", out var deltaLinkElement) ? deltaLinkElement.GetString() : null;
                url = null;
            }
        }

        await LoadEmailMetadataAsync(token, siteId, driveId, upserts, cancellationToken);
        await EnrichWithExtractionsAsync(upserts, cancellationToken);

        return new GraphDeltaResult(driveId, upserts, deletions, finalDeltaLink, isInitialSync, resyncRequired);
    }

    /// <summary>
    /// The item-scoped delta endpoint recurses into subfolders of the Incoming folder, but the
    /// previous :/children listing only ever saw direct children. Filtering on parentReference.path
    /// keeps that same scope. Deleted items are handled separately and never reach this check.
    /// </summary>
    private bool IsDirectChildOfConfiguredFolder(JsonElement item)
    {
        if (!item.TryGetProperty("parentReference", out var parentRef) || !parentRef.TryGetProperty("path", out var pathElement))
        {
            _logger.LogWarning(
                "Graph delta item {Id} had no parentReference.path; excluding it from the Incoming sync defensively.",
                item.TryGetProperty("id", out var idElement) ? idElement.GetString() : "(unknown)");
            return false;
        }

        var path = Uri.UnescapeDataString(pathElement.GetString() ?? "");
        var expectedSuffix = "root:/" + FolderPath();
        return path.EndsWith(expectedSuffix, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class GraphDeltaResyncRequiredException : Exception;

    private async Task EnrichWithExtractionsAsync(IReadOnlyList<CapturedDocument> documents, CancellationToken cancellationToken)
    {
        try
        {
            var keys = documents
                .Where(d => !string.IsNullOrWhiteSpace(d.GraphDriveId) && !string.IsNullOrWhiteSpace(d.GraphItemId))
                .Select(d => (d.GraphDriveId!, d.GraphItemId!));
            var extractions = await _extractions.GetBatchAsync(keys, cancellationToken);
            foreach (var document in documents)
            {
                if (document.GraphDriveId is null || document.GraphItemId is null ||
                    !extractions.TryGetValue((document.GraphDriveId, document.GraphItemId), out var extraction)) continue;
                document.Extraction = extraction;
                document.DetectedSupplier = extraction.SupplierName;
                document.InvoiceNumber = extraction.InvoiceNumber;
                document.InvoiceDate = extraction.InvoiceDate;
                document.BatchOrReference = extraction.Reference;
                document.Currency = extraction.Currency;
                document.Total = extraction.Total;
                document.DocumentType = DocumentType.Invoice;
                document.Status = ClassificationStatus.Classified;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Invoice extraction enrichment is unavailable; returning SharePoint documents without extraction data.");
        }
    }

    private async Task LoadEmailMetadataAsync(
        string token,
        string siteId,
        string driveId,
        IReadOnlyList<CapturedDocument> documents,
        CancellationToken cancellationToken)
    {
        // Microsoft Graph batches support at most 20 requests. Reading list-item fields in
        // batches avoids one serial HTTP round-trip for every captured attachment.
        foreach (var batch in documents.Where(d => !string.IsNullOrWhiteSpace(d.GraphItemId)).Chunk(20))
        {
            var indexed = batch.Select((document, index) => new { Document = document, Id = index.ToString() }).ToList();
            var requestBody = new
            {
                requests = indexed.Select(x => new
                {
                    id = x.Id,
                    method = "GET",
                    url = $"/sites/{Uri.EscapeDataString(siteId)}/drives/{Uri.EscapeDataString(driveId)}/items/{Uri.EscapeDataString(x.Document.GraphItemId!)}/listItem/fields"
                })
            };

            var requestJson = JsonSerializer.Serialize(requestBody);
            using var response = await SendWithRetryAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, "https://graph.microsoft.com/v1.0/$batch")
                {
                    Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                return request;
            }, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Graph email-metadata batch read failed: {(int)response.StatusCode} {response.ReasonPhrase}. {json}");
            }

            using var payload = JsonDocument.Parse(json);
            if (!payload.RootElement.TryGetProperty("responses", out var responses))
            {
                continue;
            }

            foreach (var itemResponse in responses.EnumerateArray())
            {
                var id = itemResponse.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
                var status = itemResponse.TryGetProperty("status", out var statusElement) ? statusElement.GetInt32() : 0;
                var target = indexed.FirstOrDefault(x => x.Id == id)?.Document;
                if (target is null)
                {
                    continue;
                }

                if (status < 200 || status >= 300)
                {
                    var errorBody = itemResponse.TryGetProperty("body", out var error) ? error.GetRawText() : "No response body";
                    _logger.LogWarning(
                        "Graph email-metadata read failed for drive item {DriveItemId}: HTTP {Status}. {Error}",
                        target.GraphItemId,
                        status,
                        errorBody);
                    continue;
                }

                if (!itemResponse.TryGetProperty("body", out var fields))
                {
                    _logger.LogWarning("Graph email-metadata read returned no fields for drive item {DriveItemId}.", target.GraphItemId);
                    continue;
                }

                ApplyEmailMetadata(target, fields);
            }
        }
    }

    private static void ApplyEmailMetadata(CapturedDocument document, JsonElement fields)
    {
        document.Subject = FirstNonBlank(Str(fields, "EmailSubject"), document.Subject) ?? document.Subject;
        document.FromName = FirstNonBlank(Str(fields, "EmailFromName"), document.FromName);
        document.FromAddress = FirstNonBlank(Str(fields, "EmailFromAddress"), document.FromAddress) ?? "";
        document.ToAddresses = FirstNonBlank(Str(fields, "EmailTo"), document.ToAddresses);
        document.CcAddresses = FirstNonBlank(Str(fields, "EmailCc"), document.CcAddresses);
        document.MessageId = FirstNonBlank(Str(fields, "EmailMessageId"), document.MessageId);
        document.InternetMessageId = FirstNonBlank(Str(fields, "EmailInternetMessageId"), document.InternetMessageId);
        document.ConversationId = FirstNonBlank(Str(fields, "EmailConversationId"), document.ConversationId);
        document.EmailBodyPreview = FirstNonBlank(Str(fields, "EmailBodyPreview"), document.EmailBodyPreview);
        document.OriginalMailbox = FirstNonBlank(Str(fields, "SourceMailbox"), document.OriginalMailbox) ?? document.OriginalMailbox;

        var received = Str(fields, "EmailReceivedAt");
        if (DateTimeOffset.TryParse(received, out var receivedAt))
        {
            document.ReceivedAt = receivedAt;
            document.ReceivedDate = DateOnly.FromDateTime(receivedAt.UtcDateTime);
        }
    }

    private static string? Str(JsonElement fields, params string[] names)
    {
        foreach (var name in names)
        {
            if (!fields.TryGetProperty(name, out var value))
            {
                continue;
            }

            return value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => "Yes",
                JsonValueKind.False => "No",
                _ => null
            };
        }

        return null;
    }

    private async Task<string> ResolveSiteIdAsync(string token, CancellationToken cancellationToken)
    {
        var hostname = _configuration["SharePointInbox:SiteHostname"] ?? _siteOptions.SiteHostname;
        var sitePath = _configuration["SharePointInbox:SitePath"] ?? _siteOptions.SitePath;
        if (string.IsNullOrWhiteSpace(hostname))
        {
            throw new InvalidOperationException("SharePoint Inbox needs SharePointInbox:SiteHostname or BenchmarkGraph:SiteHostname.");
        }

        var url = string.IsNullOrWhiteSpace(sitePath)
            ? $"https://graph.microsoft.com/v1.0/sites/{hostname}"
            : $"https://graph.microsoft.com/v1.0/sites/{hostname}:/{sitePath.Trim('/')}";
        using var response = await SendWithRetryAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return request;
        }, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Graph site lookup failed: {(int)response.StatusCode} {response.ReasonPhrase}. {json}");
        }

        using var payload = JsonDocument.Parse(json);
        return payload.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Graph site lookup did not return an id.");
    }

    private async Task<string> ResolveDriveIdAsync(string token, string siteId, CancellationToken cancellationToken)
    {
        var configuredDriveId = _configuration["SharePointInbox:DriveId"];
        if (!string.IsNullOrWhiteSpace(configuredDriveId))
        {
            return configuredDriveId;
        }

        var libraryName = _configuration["SharePointInbox:LibraryName"] ?? "Invoice Automation";
        using var response = await SendWithRetryAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, $"https://graph.microsoft.com/v1.0/sites/{siteId}/drives?$select=id,name&$top=200");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return request;
        }, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Graph document-library lookup failed: {(int)response.StatusCode} {response.ReasonPhrase}. {json}");
        }

        using var payload = JsonDocument.Parse(json);
        if (payload.RootElement.TryGetProperty("value", out var drives))
        {
            foreach (var drive in drives.EnumerateArray())
            {
                var name = drive.TryGetProperty("name", out var nameElement) ? nameElement.GetString() : null;
                if (string.Equals(name, libraryName, StringComparison.OrdinalIgnoreCase))
                {
                    return drive.GetProperty("id").GetString()
                        ?? throw new InvalidOperationException($"SharePoint library '{libraryName}' did not return an id.");
                }
            }
        }

        throw new InvalidOperationException($"SharePoint document library '{libraryName}' was not found.");
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var tenantId = FirstNonBlank(_graphOptions.TenantId, _configuration["AzureAd:TenantId"]);
        var clientId = FirstNonBlank(_graphOptions.ClientId, _configuration["AzureAd:ClientId"]);
        var clientSecret = FirstNonBlank(_graphOptions.ClientSecret, _configuration["AzureAd:ClientSecret"]);
        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new InvalidOperationException("SharePoint Inbox needs TenantId, ClientId and ClientSecret in GraphDocuments or AzureAd.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["scope"] = "https://graph.microsoft.com/.default",
                ["grant_type"] = "client_credentials"
            })
        };
        var response = await Http.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Graph token request failed: {(int)response.StatusCode} {response.ReasonPhrase}. {json}");
        }

        using var payload = JsonDocument.Parse(json);
        return payload.RootElement.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("Graph token response did not include an access_token.");
    }

    /// <summary>
    /// Sends a Graph request, honouring Retry-After with bounded retries when Graph responds
    /// with 429 Too Many Requests. <paramref name="requestFactory"/> is invoked once per attempt
    /// since an <see cref="HttpRequestMessage"/> cannot be sent more than once.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithRetryAsync(Func<HttpRequestMessage> requestFactory, CancellationToken cancellationToken)
    {
        const int maxAttempts = 4;
        for (var attempt = 1; ; attempt++)
        {
            using var request = requestFactory();
            var response = await Http.SendAsync(request, cancellationToken);
            if (response.StatusCode != HttpStatusCode.TooManyRequests || attempt >= maxAttempts)
            {
                return response;
            }

            var delay = ResolveRetryAfter(response) ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
            _logger.LogWarning(
                "Graph request to {Uri} was throttled (429); retrying in {Delay} (attempt {Attempt} of {MaxAttempts}).",
                request.RequestUri, delay, attempt, maxAttempts);
            response.Dispose();
            await Task.Delay(delay, cancellationToken);
        }
    }

    private static TimeSpan? ResolveRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter is null)
        {
            return null;
        }

        TimeSpan? delay = retryAfter.Delta
            ?? (retryAfter.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        if (delay is null)
        {
            return null;
        }

        if (delay < TimeSpan.Zero) return TimeSpan.Zero;
        return delay > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : delay;
    }

    private static DateTimeOffset? ReadDate(JsonElement item, string propertyName) =>
        item.TryGetProperty(propertyName, out var value)
        && DateTimeOffset.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;

    private static Guid StableGuid(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return new Guid(hash.AsSpan(0, 16));
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
