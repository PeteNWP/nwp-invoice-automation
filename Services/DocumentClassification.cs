namespace Nwp.InvoiceAutomation.Web.Services;

/// <summary>
/// Lightweight, high-precision detection for document kinds that must be kept out of the
/// supplier-invoice extraction path. Currently identifies HMRC Customs Declaration Service
/// (CDS) entry documents / ENS security filings, which carry invoice-shaped fields (an
/// "Invoice total", commodity lines) but are not supplier invoices and should not be
/// scored, mapped to an NWP supplier, or approved as one.
/// </summary>
public static class DocumentClassification
{
    // Deliberately strong, unambiguous markers so normal produce invoices never match.
    // Any single hit is enough to classify the document as a customs/ENS declaration.
    private static readonly string[] CustomsMarkers =
    [
        "customs declaration service",
        "cds_entry_document",
        "mrn:",
        "lrn:",
        "declaration: h1",
        "office of presentation",
        "declarant/representative"
    ];

    /// <summary>
    /// Returns true if any supplied text (subject, filename, raw AI response, body preview…)
    /// contains a reliable customs-declaration marker. Nulls and blanks are ignored.
    /// </summary>
    public static bool IsCustomsDeclaration(params string?[] texts)
    {
        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var haystack = text.ToLowerInvariant();
            foreach (var marker in CustomsMarkers)
            {
                if (haystack.Contains(marker, StringComparison.Ordinal)) return true;
            }
        }
        return false;
    }

    public const string CustomsExceptionReason =
        "This is a customs/ENS declaration (HMRC CDS entry), not a supplier invoice. " +
        "Any supplier, total or line values here were read from a customs form and should not be approved as an invoice.";
}
