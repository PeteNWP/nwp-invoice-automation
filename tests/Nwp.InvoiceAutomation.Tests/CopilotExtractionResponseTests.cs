using Nwp.InvoiceAutomation.Web.Models;
using Xunit;

namespace Nwp.InvoiceAutomation.Tests;

/// <summary>
/// Locks the tolerant mapping of the Copilot Studio "NWP Invoice Extraction" response into an
/// <see cref="InvoiceExtractionRequest"/>. The sample mirrors the verified Yukon test
/// (memory 2026-10-20): flat object, four line items, one with no amount.
/// </summary>
public sealed class CopilotExtractionResponseTests
{
    private const string Model = "CopilotStudio:NWP Invoice Extraction";

    private const string YukonJson = """
        {
          "documentType": "Tax Invoice",
          "supplierName": "Yukon (Pty) Ltd",
          "supplierVatNumber": "4400116804",
          "supplierAccountNumber": "90532660",
          "invoiceNumber": "SI51663",
          "invoiceDate": "2026-09-16",
          "purchaseOrderNumber": "SO54040",
          "currency": "GBP",
          "netAmount": 3920.0,
          "vatAmount": 0.0,
          "totalAmount": 3920.0,
          "lineItems": [
            { "description": "Item A", "quantity": 1, "unitPrice": 970, "lineAmount": 970 },
            { "description": "Item B", "quantity": 1, "unitPrice": 1350, "lineAmount": 1350 },
            { "description": "Item C", "quantity": 1, "unitPrice": 1600, "lineAmount": 1600 },
            { "sku": "YI-PACK905", "description": "Pallets - Wooden export", "quantity": 2 }
          ]
        }
        """;

    [Fact]
    public void Maps_verified_invoice_fields_and_lines()
    {
        var parsed = CopilotExtractionResponse.TryParse(YukonJson, "drive-1", "item-1", "site-1", Model);

        Assert.NotNull(parsed);
        Assert.True(parsed!.LooksLikeInvoice);
        Assert.Equal("Tax Invoice", parsed.DocumentType);

        var e = parsed.Extraction;
        Assert.Equal("drive-1", e.DriveId);
        Assert.Equal("item-1", e.DriveItemId);
        Assert.Equal("Yukon (Pty) Ltd", e.SupplierName);
        Assert.Equal("SI51663", e.InvoiceNumber);
        Assert.Equal(new DateOnly(2026, 9, 16), e.InvoiceDate);
        Assert.Equal("SO54040", e.Reference);
        Assert.Equal("GBP", e.Currency);
        Assert.Equal(3920.0m, e.Total);
        Assert.Equal(Model, e.Model);

        Assert.Equal(4, e.Lines.Count);
        Assert.Equal(970m, e.Lines[0].LineTotal);
        Assert.Equal(1, e.Lines[0].LineNumber);
        Assert.Equal(4, e.Lines[3].LineNumber);
        Assert.Equal("YI-PACK905", e.Lines[3].Sku);
        Assert.Null(e.Lines[3].LineTotal);          // no amount returned
        Assert.Equal(970m + 1350m + 1600m, e.Lines.Where(l => l.LineTotal.HasValue).Sum(l => l.LineTotal));
    }

    [Fact]
    public void Blank_line_description_is_replaced_with_placeholder()
    {
        const string json = """
            { "invoiceNumber": "X1", "totalAmount": 10,
              "lineItems": [ { "quantity": 1, "lineAmount": 10 } ] }
            """;
        var parsed = CopilotExtractionResponse.TryParse(json, "d", "i", null, Model);
        Assert.NotNull(parsed);
        Assert.Equal("(no description)", parsed!.Extraction.Lines[0].Description);
    }

    [Fact]
    public void Non_invoice_document_is_flagged_not_an_invoice()
    {
        const string json = """{ "documentType": "other" }""";
        var parsed = CopilotExtractionResponse.TryParse(json, "d", "i", null, Model);
        Assert.NotNull(parsed);
        Assert.False(parsed!.LooksLikeInvoice);
        Assert.Equal("other", parsed.DocumentType);
    }

    [Fact]
    public void Wrapped_payload_is_unwrapped()
    {
        const string json = """{ "body": { "invoiceNumber": "Y9", "totalAmount": 5 } }""";
        var parsed = CopilotExtractionResponse.TryParse(json, "d", "i", null, Model);
        Assert.NotNull(parsed);
        Assert.Equal("Y9", parsed!.Extraction.InvoiceNumber);
        Assert.Equal(5m, parsed.Extraction.Total);
    }

    [Fact]
    public void Garbage_returns_null()
    {
        Assert.Null(CopilotExtractionResponse.TryParse("not json", "d", "i", null, Model));
        Assert.Null(CopilotExtractionResponse.TryParse("[1,2,3]", "d", "i", null, Model));
    }
}
