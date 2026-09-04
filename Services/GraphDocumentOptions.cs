namespace Nwp.InvoiceAutomation.Web.Services;

public sealed class GraphDocumentOptions
{
    public bool Enabled { get; set; }
    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
}
