using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Nwp.InvoiceAutomation.Web.Models;

namespace Nwp.InvoiceAutomation.Web.Services;

/// <summary>
/// Reads captured documents from the SharePoint Invoice Automation/Incoming folder.
/// Email envelope metadata and extracted invoice fields will be added when capture records
/// are persisted in SQL; for now every discovered file enters the workflow as Unclassified.
/// </summary>
public sealed class SharePointDocumentStore : IDocumentStore
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly GraphDocumentOptions _graphOptions;
    private readonly BenchmarkGraphOptions _siteOptions;
    private readonly IConfiguration _configuration;
    private readonly MockDocumentStore _fallback;
    private readonly ILogger<SharePointDocumentStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<CapturedDocument>? _cache;
    private DateTimeOffset _cacheExpiresAt;

    public SharePointDocumentStore(
        IHttpClientFactory httpClientFactory,
        IOptions<GraphDocumentOptions> graphOptions,
        IOptions<BenchmarkGraphOptions> siteOptions,
        IConfiguration configuration,
        MockDocumentStore fallback,
        ILogger<SharePointDocumentStore> logger)
    {
        _httpClientFactory = httpClientFactory;
        _graphOptions = graphOptions.Value;
        _siteOptions = siteOptions.Value;
        _configuration = configuration;
        _fallback = fallback;
        _logger = logger;
    }

    private HttpClient Http => _httpClientFactory.CreateClient("sharepoint-inbox");

    public string Source { get; private set; } = "SharePoint Incoming";

    public async Task<IReadOnlyList<CapturedDocument>> AllAsync(CancellationToken cancellationToken = default)
    {
        if (UseMockSource())
        {
            Source = _fallback.Source;
            return await _fallback.AllAsync(cancellationToken);
        }

        if (_cache is not null && DateTimeOffset.UtcNow < _cacheExpiresAt)
        {
            return _cache;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_cache is not null && DateTimeOffset.UtcNow < _cacheExpiresAt)
            {
                return _cache;
            }

            try
            {
                if (!_graphOptions.Enabled)
                {
                    throw new InvalidOperationException("DocumentSource is SharePoint but GraphDocuments:Enabled is not true.");
                }

                _cache = await LoadFromSharePointAsync(cancellationToken);
                _cacheExpiresAt = DateTimeOffset.UtcNow.AddSeconds(30);
                Source = $"SharePoint: {_configuration["SharePointInbox:LibraryName"] ?? "Invoice Automation"}/{FolderPath()}";
                return _cache;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (_configuration.GetValue<bool>("SharePointInbox:FallbackToMock"))
                {
                    _logger.LogWarning(ex, "SharePoint Incoming read failed; falling back to mock/test records by configuration.");
                    Source = "Mock/test records (SharePoint Incoming unreachable)";
                    return await _fallback.AllAsync(cancellationToken);
                }

                _logger.LogError(ex, "SharePoint Incoming read failed.");
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<CapturedDocument?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        (await AllAsync(cancellationToken)).FirstOrDefault(d => d.Id == id);

    public async Task<IReadOnlyList<CapturedDocument>> ByTypeAsync(DocumentType type, CancellationToken cancellationToken = default) =>
        (await AllAsync(cancellationToken)).Where(d => d.DocumentType == type).ToList();

    public async Task<IReadOnlyList<CapturedDocument>> ByStatusAsync(ClassificationStatus status, CancellationToken cancellationToken = default) =>
        (await AllAsync(cancellationToken)).Where(d => d.Status == status).ToList();

    public async Task ResolveExceptionAsync(Guid id, ExceptionResolution resolution, CancellationToken cancellationToken = default)
    {
        var document = await GetAsync(id, cancellationToken);
        if (document is null)
        {
            return;
        }

        document.DocumentType = resolution.DocumentType;
        document.Status = resolution.AssignedQueue == "Ignored"
            ? ClassificationStatus.Ignored
            : ClassificationStatus.Classified;
        document.ExceptionReason = null;
        document.SupplierMasterName = resolution.SupplierMasterName;
        document.InvoiceNumber = resolution.InvoiceNumber;
        document.InvoiceDate = resolution.InvoiceDate;
        document.BatchOrReference = resolution.BatchOrReference;
        document.Total = resolution.Total;
        document.Currency = resolution.Currency;
        document.AssignedQueue = resolution.AssignedQueue;
        document.WorkflowOwner = resolution.WorkflowOwner;
        document.ReviewerNote = resolution.ResolutionNote;
    }

    private bool UseMockSource() =>
        string.Equals(_configuration["DocumentSource"], "Mock", StringComparison.OrdinalIgnoreCase);

    private string FolderPath() =>
        _configuration["SharePointInbox:FolderPath"]?.Trim('/')
        ?? "Incoming";

    private async Task<IReadOnlyList<CapturedDocument>> LoadFromSharePointAsync(CancellationToken cancellationToken)
    {
        var token = await GetAccessTokenAsync(cancellationToken);
        var siteId = await ResolveSiteIdAsync(token, cancellationToken);
        var driveId = await ResolveDriveIdAsync(token, siteId, cancellationToken);
        var encodedPath = string.Join("/", FolderPath().Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        var url = $"https://graph.microsoft.com/v1.0/sites/{siteId}/drives/{driveId}/root:/{encodedPath}:/children?$select=id,name,file,folder,createdDateTime,lastModifiedDateTime,parentReference&$top=200";
        var documents = new List<CapturedDocument>();

        while (!string.IsNullOrWhiteSpace(url))
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var response = await Http.SendAsync(request, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Graph Incoming-folder read failed: {(int)response.StatusCode} {response.ReasonPhrase}. {json}");
            }

            using var payload = JsonDocument.Parse(json);
            var root = payload.RootElement;
            if (root.TryGetProperty("value", out var items))
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (!item.TryGetProperty("file", out var file))
                    {
                        continue;
                    }

                    var itemId = item.GetProperty("id").GetString() ?? "";
                    var name = item.GetProperty("name").GetString() ?? "Unnamed document";
                    var mimeType = file.TryGetProperty("mimeType", out var mime) ? mime.GetString() ?? "application/octet-stream" : "application/octet-stream";
                    var received = ReadDate(item, "createdDateTime") ?? ReadDate(item, "lastModifiedDateTime") ?? DateTimeOffset.UtcNow;

                    documents.Add(new CapturedDocument
                    {
                        Id = StableGuid(itemId),
                        Subject = name,
                        OriginalMailbox = _configuration["Mailbox:PrimaryMailbox"] ?? "invoices-evesham@nationwideproduce.com",
                        ReceivedDate = DateOnly.FromDateTime(received.UtcDateTime),
                        AttachmentName = name,
                        AttachmentMime = mimeType,
                        GraphSiteId = siteId,
                        GraphDriveId = driveId,
                        GraphItemId = itemId,
                        DocumentType = DocumentType.Unknown,
                        Status = ClassificationStatus.Unclassified,
                        AssignedQueue = "Incoming",
                        ReviewerNote = "Captured in SharePoint; email metadata and invoice extraction are not connected yet."
                    });
                }
            }

            url = root.TryGetProperty("@odata.nextLink", out var next) ? next.GetString() : null;
        }

        return documents
            .OrderByDescending(d => d.ReceivedDate)
            .ThenBy(d => d.AttachmentName, StringComparer.OrdinalIgnoreCase)
            .ToList();
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
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await Http.SendAsync(request, cancellationToken);
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
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://graph.microsoft.com/v1.0/sites/{siteId}/drives?$select=id,name&$top=200");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await Http.SendAsync(request, cancellationToken);
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
