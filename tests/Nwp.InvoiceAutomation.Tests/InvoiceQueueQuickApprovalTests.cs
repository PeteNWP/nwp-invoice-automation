using Nwp.InvoiceAutomation.Web.Models;
using Nwp.InvoiceAutomation.Web.Pages.Invoices;
using Xunit;
using ReviewedIndexModel = Nwp.InvoiceAutomation.Web.Pages.Reviewed.IndexModel;

namespace Nwp.InvoiceAutomation.Tests;

public sealed class InvoiceQueueQuickApprovalTests
{
    [Fact]
    public void CompleteMappedHeader_CanBeQuickApproved_WithoutLineItems()
    {
        var document = CompleteDocument();
        document.Extraction = WithHeader(lines: []);

        var blockers = IndexModel.GetQuickApprovalBlockers(document, authoritativeSupplierExists: true);

        Assert.Empty(blockers);
    }

    [Fact]
    public void InvoiceNumberOrReference_SatisfiesTheIdentifierRequirement()
    {
        var withInvoiceNumber = CompleteDocument();
        withInvoiceNumber.Extraction = WithHeader(invoiceNumber: "INV-42", reference: null);
        var withReference = CompleteDocument();
        withReference.Extraction = WithHeader(invoiceNumber: null, reference: "REF-42");

        Assert.Empty(IndexModel.GetQuickApprovalBlockers(withInvoiceNumber, true));
        Assert.Empty(IndexModel.GetQuickApprovalBlockers(withReference, true));
    }

    [Fact]
    public void MissingRequiredHeadersAndMapping_BlockQuickApproval()
    {
        var document = new CapturedDocument
        {
            Subject = " ",
            ReceivedDate = default,
            NwpSupplierOid = "supplier-1",
            NwpSupplierReference = "SUP-1",
            SupplierMasterName = "Supplier One",
            Extraction = new InvoiceExtraction
            {
                Id = 9,
                SupplierName = null,
                InvoiceNumber = null,
                Reference = null,
                InvoiceDate = null,
                Total = null
            }
        };

        var blockers = IndexModel.GetQuickApprovalBlockers(document, authoritativeSupplierExists: false);

        Assert.Contains("the subject is missing", blockers);
        Assert.Contains("the observed supplier is missing", blockers);
        Assert.Contains("an authoritative NWP supplier mapping is missing", blockers);
        Assert.Contains("the invoice/reference number is missing", blockers);
        Assert.Contains("the invoice date is missing", blockers);
        Assert.Contains("the total is missing", blockers);
        Assert.Contains("the received date is missing", blockers);
    }

    [Fact]
    public void ZeroTotal_IsStillAPresentTotal()
    {
        var document = CompleteDocument();
        document.Extraction = WithHeader(total: 0m);

        Assert.Empty(IndexModel.GetQuickApprovalBlockers(document, true));
    }

    [Fact]
    public void AlreadyApprovedExtraction_CannotBeQuickApprovedAgain()
    {
        var document = CompleteDocument();
        document.Extraction = WithHeader(reviewStatus: ExtractionReviewStatus.Approved);

        var blockers = IndexModel.GetQuickApprovalBlockers(document, authoritativeSupplierExists: true);

        Assert.Contains("the extraction is already approved", blockers);
    }

    [Fact]
    public void StaleSupplierOidWithoutAuthoritativeMatch_BlocksQuickApproval()
    {
        var document = CompleteDocument();

        var blockers = IndexModel.GetQuickApprovalBlockers(document, authoritativeSupplierExists: false);

        Assert.Contains("an authoritative NWP supplier mapping is missing", blockers);
    }

    [Fact]
    public void ApprovedClassifiedInvoice_MovesFromActiveQueueToReviewed()
    {
        var document = CompleteDocument();
        document.DocumentType = DocumentType.Invoice;
        document.Status = ClassificationStatus.Classified;
        document.Extraction = WithHeader(reviewStatus: ExtractionReviewStatus.Approved);

        Assert.False(IndexModel.IsActiveQueueDocument(document));
        Assert.True(ReviewedIndexModel.IsReviewedDocument(document));
    }

    [Fact]
    public void SavedButUnapprovedInvoice_RemainsInActiveQueue()
    {
        var document = CompleteDocument();
        document.DocumentType = DocumentType.Invoice;
        document.Status = ClassificationStatus.ReviewRequired;
        document.Extraction = WithHeader(reviewStatus: ExtractionReviewStatus.Corrected);

        Assert.True(IndexModel.IsActiveQueueDocument(document));
        Assert.False(ReviewedIndexModel.IsReviewedDocument(document));
    }

    private static CapturedDocument CompleteDocument() => new()
    {
        Subject = "Invoice INV-42",
        ReceivedDate = new DateOnly(2026, 10, 26),
        NwpSupplierOid = "supplier-1",
        NwpSupplierReference = "SUP-1",
        SupplierMasterName = "Supplier One",
        Extraction = WithHeader()
    };

    private static InvoiceExtraction WithHeader(
        string? invoiceNumber = "INV-42",
        string? reference = "PO-42",
        decimal? total = 120m,
        IReadOnlyList<InvoiceExtractionLine>? lines = null,
        ExtractionReviewStatus reviewStatus = ExtractionReviewStatus.Pending) => new()
    {
        Id = 9,
        SupplierName = "Supplier One observed",
        InvoiceNumber = invoiceNumber,
        Reference = reference,
        InvoiceDate = new DateOnly(2026, 10, 25),
        Total = total,
        Currency = "GBP",
        ReviewStatus = reviewStatus,
        Lines = lines ?? [new InvoiceExtractionLine { LineNumber = 1, Description = "Apples" }]
    };
}
