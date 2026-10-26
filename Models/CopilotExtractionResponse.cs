using System.Globalization;
using System.Text.Json;

namespace Nwp.InvoiceAutomation.Web.Models;

/// <summary>
/// Maps the structured JSON returned by the Copilot Studio <c>NWP Invoice Extraction</c>
/// workflow into an <see cref="InvoiceExtractionRequest"/>. The workflow's verified response
/// (Yukon test, 2026-10-20) is a flat object: documentType, supplierName, supplierVatNumber,
/// supplierAccountNumber, invoiceNumber, invoiceDate, purchaseOrderNumber, currency,
/// netAmount, vatAmount, totalAmount, and a line-items array.
///
/// Parsing is deliberately tolerant: the workflow schema can still change, and GPT may vary
/// key casing or wrap the payload, so we probe a list of candidate names rather than binding
/// to one fixed shape. Returns null only when the JSON is unusable.
/// </summary>
public static class CopilotExtractionResponse
{
    public sealed record Parsed(
        string? DocumentType,
        InvoiceExtractionRequest Extraction,
        bool LooksLikeInvoice);

    public static Parsed? TryParse(string json, string driveId, string driveItemId, string? siteId, string model)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return null; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            // Some Copilot/Power Automate responses wrap the payload (e.g. { "body": { ... } }).
            root = Unwrap(root);

            var documentType = Str(root, "documentType", "docType", "type");
            var total = Dec(root, "totalAmount", "total", "grossAmount", "invoiceTotal")
                        ?? Dec(root, "netAmount", "net");

            var lines = ParseLines(root);

            var extraction = new InvoiceExtractionRequest
            {
                DriveId = driveId,
                DriveItemId = driveItemId,
                SiteId = siteId,
                SupplierName = Str(root, "supplierName", "supplier", "vendorName", "vendor"),
                InvoiceNumber = Str(root, "invoiceNumber", "invoiceNo", "invoice"),
                InvoiceDate = Date(root, "invoiceDate", "date", "taxPoint"),
                Reference = Str(root, "purchaseOrderNumber", "poNumber", "reference", "customerOrderNumber", "batch"),
                Currency = Currency(root, "currency", "currencyCode", "ccy"),
                Total = total,
                Confidence = Dec(root, "confidence"),
                Model = model,
                ExtractedAt = DateTimeOffset.UtcNow,
                RawResponse = json,
                Lines = lines
            };

            var looksLikeInvoice =
                !string.IsNullOrWhiteSpace(extraction.InvoiceNumber)
                || extraction.Total is > 0m
                || lines.Count > 0
                || (documentType?.Contains("invoice", StringComparison.OrdinalIgnoreCase) ?? false);

            return new Parsed(documentType, extraction, looksLikeInvoice);
        }
    }

    private static JsonElement Unwrap(JsonElement root)
    {
        // If the top-level object already carries the invoice fields, use it as-is.
        if (HasInvoiceField(root)) return root;

        // Otherwise, if a known wrapper holds an object that does, descend into it.
        foreach (var wrapper in new[] { "body", "result", "output", "invoice", "data" })
        {
            if (root.TryGetProperty(wrapper, out var inner)
                && inner.ValueKind == JsonValueKind.Object
                && HasInvoiceField(inner))
            {
                return inner;
            }
        }
        return root;
    }

    private static bool HasInvoiceField(JsonElement e) =>
        e.TryGetProperty("supplierName", out _)
        || e.TryGetProperty("invoiceNumber", out _)
        || e.TryGetProperty("totalAmount", out _)
        || e.TryGetProperty("documentType", out _);

    private static List<InvoiceExtractionLineRequest> ParseLines(JsonElement root)
    {
        var result = new List<InvoiceExtractionLineRequest>();
        JsonElement array = default;
        var found = false;
        foreach (var name in new[] { "lineItems", "lines", "invoiceLines", "items", "lineList" })
        {
            if (root.TryGetProperty(name, out var candidate) && candidate.ValueKind == JsonValueKind.Array)
            {
                array = candidate;
                found = true;
                break;
            }
        }
        if (!found) return result;

        var lineNumber = 0;
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            lineNumber++;
            var description = Str(item, "description", "productDescription", "product", "name", "item");
            result.Add(new InvoiceExtractionLineRequest
            {
                LineNumber = lineNumber,
                Sku = Str(item, "sku", "code", "productCode", "itemCode"),
                // The receiver API rejects blank descriptions; keep the line (quantities/totals
                // still reconcile) with an explicit placeholder rather than dropping it.
                Description = string.IsNullOrWhiteSpace(description) ? "(no description)" : description!,
                Quantity = Dec(item, "quantity", "qty", "units"),
                UnitPrice = Dec(item, "unitPrice", "price", "rate", "unitAmount"),
                LineTotal = Dec(item, "lineTotal", "lineAmount", "amount", "total", "netAmount")
            });
        }
        return result;
    }

    private static string? Str(JsonElement e, params string[] names)
    {
        foreach (var name in names)
        {
            if (e.TryGetProperty(name, out var v))
            {
                var s = v.ValueKind switch
                {
                    JsonValueKind.String => v.GetString(),
                    JsonValueKind.Number => v.ToString(),
                    _ => null
                };
                if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
            }
        }
        return null;
    }

    private static decimal? Dec(JsonElement e, params string[] names)
    {
        foreach (var name in names)
        {
            if (!e.TryGetProperty(name, out var v)) continue;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) return d;
            if (v.ValueKind == JsonValueKind.String)
            {
                var raw = v.GetString();
                if (string.IsNullOrWhiteSpace(raw)) continue;
                // Strip currency symbols/thousands separators that occasionally slip through.
                var cleaned = new string(raw.Where(c => char.IsDigit(c) || c is '.' or '-').ToArray());
                if (decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)) return parsed;
            }
        }
        return null;
    }

    private static DateOnly? Date(JsonElement e, params string[] names)
    {
        foreach (var name in names)
        {
            var s = Str(e, name);
            if (s is null) continue;
            if (DateOnly.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
                return DateOnly.FromDateTime(dt);
        }
        return null;
    }

    private static string? Currency(JsonElement e, params string[] names)
    {
        var s = Str(e, names);
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().ToUpperInvariant();
        return s.Length == 3 ? s : null; // receiver requires exactly 3 letters
    }
}
