namespace Nwp.InvoiceAutomation.Web.Models;

/// <summary>A read-only supplier from NWPDev.dbo.Supplier.</summary>
public sealed record NwpSupplier(
    string Oid,
    string Reference,
    string Name,
    string? Status,
    string? VatNumber,
    string? Email,
    string? PaymentCurrency)
{
    public string DisplayLabel => string.IsNullOrWhiteSpace(Reference) ? Name : $"{Reference} — {Name}";
}
