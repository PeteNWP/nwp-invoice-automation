using Nwp.InvoiceAutomation.Web.Services;
using Xunit;

namespace Nwp.InvoiceAutomation.Tests;

/// <summary>
/// Locks the high-precision customs/ENS detection so HMRC CDS entry documents are kept out of
/// the supplier-invoice path, while ordinary produce invoices are never misclassified.
/// </summary>
public sealed class DocumentClassificationTests
{
    [Theory]
    [InlineData("CDS_Entry_Document_6GB823723242000_BKEC26O090854.pdf")]
    [InlineData("MRN: 26GBBUMPNYZ72ZIAR1")]
    [InlineData("Customs Declaration Service")]
    [InlineData("LRN: EETPMLPR10000000131237")]
    public void DetectsCustomsDeclarations(string text)
    {
        Assert.True(DocumentClassification.IsCustomsDeclaration(text));
    }

    [Theory]
    [InlineData("Wealmoor Ltd Invoice 884512.pdf")]
    [InlineData("Funghi Funghi NV - Factuur SI2610477")]
    [InlineData("POC Fattura riepilogativa DdT clienti - 512")]
    [InlineData(null)]
    [InlineData("")]
    public void IgnoresOrdinaryInvoices(string? text)
    {
        Assert.False(DocumentClassification.IsCustomsDeclaration(text));
    }

    [Fact]
    public void MatchesWhenOnlyOneFieldCarriesTheMarker()
    {
        // Subject and filename are innocuous; the customs marker is only in the raw AI response.
        Assert.True(DocumentClassification.IsCustomsDeclaration(
            "Import docs", "scan.pdf", "{\"mrn\":\"26GBBUMPNYZ72ZIAR1\",\"text\":\"MRN: 26GB...\"}"));
    }
}
