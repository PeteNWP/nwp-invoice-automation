using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Nwp.InvoiceAutomation.Web.Services;

public sealed class GraphDocumentClient : IGraphDocumentClient
{
    private readonly HttpClient _httpClient;
    private readonly GraphDocumentOptions _graphOptions;
    private readonly IConfiguration _configuration;

    public GraphDocumentClient(HttpClient httpClient, IOptions<GraphDocumentOptions> graphOptions, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _graphOptions = graphOptions.Value;
        _configuration = configuration;
    }

    public async Task<GraphDocumentContent> DownloadFromSharingUrlAsync(string sharingUrl, CancellationToken cancellationToken = default)
    {
        if (!_graphOptions.Enabled)
        {
            throw new InvalidOperationException("Graph document preview is disabled. Set GraphDocuments:Enabled=true in appsettings.Local.json.");
        }

        var token = await GetAccessTokenAsync(cancellationToken);
        var shareId = ToGraphShareId(sharingUrl);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://graph.microsoft.com/v1.0/shares/{shareId}/driveItem/content");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Graph download failed: {(int)response.StatusCode} {response.ReasonPhrase}. {error}");
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/pdf";
        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
            ?? "document.pdf";

        return new GraphDocumentContent(stream, contentType, fileName);
    }

    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var tenantId = FirstNonBlank(_graphOptions.TenantId, _configuration["AzureAd:TenantId"]);
        var clientId = FirstNonBlank(_graphOptions.ClientId, _configuration["AzureAd:ClientId"]);
        var clientSecret = FirstNonBlank(_graphOptions.ClientSecret, _configuration["AzureAd:ClientSecret"]);

        if (string.IsNullOrWhiteSpace(tenantId) || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            throw new InvalidOperationException("Graph preview needs TenantId, ClientId and ClientSecret. Set GraphDocuments or AzureAd values in appsettings.Local.json.");
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

        var response = await _httpClient.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Graph token request failed: {(int)response.StatusCode} {response.ReasonPhrase}. {json}");
        }

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("access_token").GetString()
            ?? throw new InvalidOperationException("Graph token response did not include an access_token.");
    }

    private static string ToGraphShareId(string sharingUrl)
    {
        var bytes = Encoding.UTF8.GetBytes(sharingUrl);
        var base64 = Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('/', '_')
            .Replace('+', '-');

        return $"u!{base64}";
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
