namespace Nwp.InvoiceAutomation.Web.Services;

/// <summary>
/// Config for reading the invoice benchmark live from SharePoint lists via Microsoft Graph.
/// When disabled or unreachable, BenchmarkStore falls back to the seed CSVs in the data folder.
/// Credentials are shared with GraphDocuments/AzureAd (same app registration).
/// </summary>
public sealed class BenchmarkGraphOptions
{
    public bool Enabled { get; set; }

    /// <summary>SharePoint host, e.g. nationwideproduceplc.sharepoint.com.</summary>
    public string SiteHostname { get; set; } = "";

    /// <summary>Server-relative site path, e.g. "/sites/Communication". Blank = tenant root site.</summary>
    public string SitePath { get; set; } = "";

    /// <summary>List ID (GUID) of the Invoice Benchmark header list.</summary>
    public string HeadersListId { get; set; } = "";

    /// <summary>List ID (GUID) of the Invoice Benchmark Lines list.</summary>
    public string LinesListId { get; set; } = "";

    // Graph credentials — fall back to GraphDocuments / AzureAd when left blank.
    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
}
