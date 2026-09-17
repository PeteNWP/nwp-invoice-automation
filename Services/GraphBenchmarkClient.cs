using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Nwp.InvoiceAutomation.Web.Services;

/// <summary>
/// Reads SharePoint list items (the live invoice benchmark) via Microsoft Graph using the same
/// client-credentials app registration as GraphDocumentClient. Returns each item's `fields` as a
/// raw JSON object so BenchmarkStore can map columns tolerantly.
/// </summary>
public sealed class GraphBenchmarkClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly BenchmarkGraphOptions _options;
    private readonly GraphDocumentOptions _graphDocuments;
    private readonly IConfiguration _configuration;

    public GraphBenchmarkClient(
        IHttpClientFactory httpClientFactory,
        IOptions<BenchmarkGraphOptions> options,
        IOptions<GraphDocumentOptions> graphDocuments,
        IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _graphDocuments = graphDocuments.Value;
        _configuration = configuration;
    }

    private HttpClient Http => _httpClientFactory.CreateClient("benchmark-graph");

    public bool Enabled => _options.Enabled;

    /// <summary>Returns the `fields` object of every item in the given list, following paging.</summary>
    public async Task<IReadOnlyList<JsonElement>> GetListItemFieldsAsync(string listId, CancellationToken cancellationToken = default)
    {
        var token = await GetAccessTokenAsync(cancellationToken);
        var siteId = await ResolveSiteIdAsync(token, cancellationToken);

        var items = new List<JsonElement>();
        var url = $"https://graph.microsoft.com/v1.0/sites/{siteId}/lists/{listId}/items?$expand=fields&$top=200";

        while (!string.IsNullOrEmpty(url))
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await Http.SendAsync(request, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Graph list read failed: {(int)response.StatusCode} {response.ReasonPhrase}. {json}");
            }

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("value", out var value))
            {
                foreach (var item in value.EnumerateArray())
                {
                    if (item.TryGetProperty("fields", out var fields))
                    {
                        // Clone so the element survives disposal of the JsonDocument.
                        items.Add(fields.Clone());
                    }
                }
            }

            url = root.TryGetProperty("@odata.nextLink", out var next) ? next.GetString() : null;
        }

        return items;
    }

    private async Task<string> ResolveSiteIdAsync(string token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.SiteHostname))
        {
            throw new InvalidOperationException("Benchmark Graph read needs BenchmarkGraph:SiteHostname.");
        }

        var path = string.IsNullOrWhiteSpace(_options.SitePath)
            ? $"https://graph.microsoft.com/v1.0/sites/{_options.SiteHostname}"
            : $"https://graph.microsoft.com/v1.0/sites/{_options.SiteHostname}:{_options.SitePath.TrimEnd('/')}";

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await Http.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Graph site lookup failed: {(int)response.StatusCode} {response.ReasonPhrase}. {json}");
        }

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Graph site response did not include an id.");
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var tenantId = FirstNonBlank(_options.TenantId, _graphDocuments.TenantId, _configuration["AzureAd:TenantId"]);
        var clientId = FirstNonBlank(_options.ClientId, _graphDocuments.ClientId, _configuration["AzureAd:ClientId"]);
        var clientSecret = FirstNonBlank(_options.ClientSecret, _graphDocuments.ClientSecret, _configuration["AzureAd:ClientSecret"]);

        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new InvalidOperationException("Benchmark Graph read needs TenantId, ClientId and ClientSecret (BenchmarkGraph, GraphDocuments or AzureAd).");
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

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("Graph token response did not include an access_token.");
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
