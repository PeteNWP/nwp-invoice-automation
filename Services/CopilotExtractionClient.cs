using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Nwp.InvoiceAutomation.Web.Services;

public interface ICopilotExtractionClient
{
    /// <summary>
    /// POSTs a document to the Copilot Studio workflow and returns the raw JSON response body.
    /// Throws on transport/HTTP failure so the worker can mark the document failed and retry.
    /// </summary>
    Task<string> ExtractAsync(string documentId, string fileName, string contentType, byte[] content, CancellationToken cancellationToken);
}

public sealed class CopilotExtractionClient : ICopilotExtractionClient
{
    private readonly HttpClient _httpClient;
    private readonly CopilotExtractionOptions _options;

    public CopilotExtractionClient(HttpClient httpClient, IOptions<CopilotExtractionOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
    }

    public async Task<string> ExtractAsync(string documentId, string fileName, string contentType, byte[] content, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.EndpointUrl))
            throw new InvalidOperationException("CopilotExtraction:EndpointUrl is not configured.");

        var payload = new
        {
            documentId,
            fileName,
            contentType,
            contentBase64 = Convert.ToBase64String(content)
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, _options.EndpointUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        if (!string.IsNullOrWhiteSpace(_options.ApiKeyHeaderName) && !string.IsNullOrWhiteSpace(_options.ApiKey))
            request.Headers.TryAddWithoutValidation(_options.ApiKeyHeaderName, _options.ApiKey);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var preview = body.Length > 500 ? body[..500] : body;
            throw new InvalidOperationException($"Copilot extraction returned {(int)response.StatusCode} {response.ReasonPhrase}. {preview}");
        }
        return body;
    }
}
