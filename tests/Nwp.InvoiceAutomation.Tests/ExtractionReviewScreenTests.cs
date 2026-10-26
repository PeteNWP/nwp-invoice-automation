using Nwp.InvoiceAutomation.Web.Models;
using Nwp.InvoiceAutomation.Web.Pages.Exceptions;
using Xunit;

namespace Nwp.InvoiceAutomation.Tests;

public sealed class ExtractionReviewScreenTests
{
    [Fact]
    public void FromDocument_UsesLiveExtraction_ButKeepsAuthoritativeSupplierSeparate()
    {
        var document = new CapturedDocument
        {
            NwpSupplierOid = "master-1",
            SupplierMasterName = "Authoritative Foods Ltd",
            DetectedSupplier = "old placeholder",
            Extraction = new InvoiceExtraction
            {
                Id = 42,
                ReviewRevision = 3,
                SupplierName = "Observed Foods",
                InvoiceNumber = "INV-9",
                Lines = [new InvoiceExtractionLine { LineNumber = 1, Description = "Apples", Quantity = 2 }]
            }
        };
        var suppliers = new[] { new NwpSupplier("master-1", "SUP1", "Authoritative Foods Ltd", null, null, null, "GBP") };

        var input = ResolveModel.InputModel.FromDocument(document, suppliers);

        Assert.Equal("master-1", input.NwpSupplierOid);
        Assert.Equal("Observed Foods", input.ObservedSupplierName);
        Assert.Equal("INV-9", input.InvoiceNumber);
        Assert.Equal(3, input.ExtractionRevision);
        Assert.Single(input.Lines);
        Assert.Equal("Apples", input.Lines[0].Description);
    }
}
