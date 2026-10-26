using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Nwp.InvoiceAutomation.Web.Models;
using Nwp.InvoiceAutomation.Web.Services;

namespace Nwp.InvoiceAutomation.Web.Pages.Invoices;

public sealed class IndexModel : PageModel
{
    private readonly IDocumentStore _docs;
    private readonly INwpSupplierDirectory _supplierDirectory;
    private readonly IInvoiceExtractionRepository _extractions;
    private readonly UserAccessService _userAccess;

    public IndexModel(
        IDocumentStore docs,
        INwpSupplierDirectory supplierDirectory,
        IInvoiceExtractionRepository extractions,
        UserAccessService userAccess)
    {
        _docs = docs;
        _supplierDirectory = supplierDirectory;
        _extractions = extractions;
        _userAccess = userAccess;
    }

    public IReadOnlyList<CapturedDocument> Documents { get; private set; } = [];
    public IReadOnlyDictionary<Guid, IReadOnlyList<string>> QuickApprovalBlockers { get; private set; }
        = new Dictionary<Guid, IReadOnlyList<string>>();

    [TempData]
    public string? SuccessMessage { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken) => await LoadAsync(cancellationToken);

    public async Task<IActionResult> OnPostQuickApproveAsync(
        Guid id,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        var document = await _docs.GetAsync(id, cancellationToken);
        if (document is null)
        {
            return NotFound();
        }

        var supplier = string.IsNullOrWhiteSpace(document.NwpSupplierOid)
            ? null
            : await _supplierDirectory.FindByOidAsync(document.NwpSupplierOid, cancellationToken);
        var blockers = GetQuickApprovalBlockers(document, supplier is not null);
        if (blockers.Count > 0)
        {
            ModelState.AddModelError(string.Empty,
                $"This invoice cannot be marked Reviewed from the queue because {string.Join(", ", blockers)}. Open Review to complete or correct it.");
            await LoadAsync(cancellationToken);
            return Page();
        }

        var extraction = document.Extraction!;
        var reviewedBy = _userAccess.GetEmail(User) ?? "Demo user";
        try
        {
            // Quick approval deliberately persists the extraction exactly as loaded. It does
            // not correct headers or lines; corrections remain the full Review screen's job.
            // If a prior request approved the extraction but failed before resolving the
            // captured document, leave that immutable approval alone and finish step two.
            if (extraction.ReviewStatus != ExtractionReviewStatus.Approved)
            {
                await _extractions.SaveReviewAsync(new InvoiceExtractionReview
                {
                    Id = extraction.Id,
                    ExpectedRevision = expectedRevision,
                    SupplierName = extraction.SupplierName,
                    InvoiceNumber = extraction.InvoiceNumber,
                    InvoiceDate = extraction.InvoiceDate,
                    Reference = extraction.Reference,
                    Currency = extraction.Currency,
                    Total = extraction.Total,
                    ReviewedBy = reviewedBy,
                    ReviewNote = extraction.ReviewNote,
                    Approve = true,
                    Lines = extraction.Lines.Select(line => new InvoiceExtractionLineRequest
                    {
                        LineNumber = line.LineNumber,
                        Sku = line.Sku,
                        Description = line.Description,
                        Quantity = line.Quantity,
                        UnitPrice = line.UnitPrice,
                        LineTotal = line.LineTotal
                    }).ToList()
                }, cancellationToken);
            }
        }
        catch (InvoiceExtractionConcurrencyException ex)
        {
            ModelState.AddModelError(string.Empty, $"{ex.Message} Open Review to check the latest values.");
            await LoadAsync(cancellationToken);
            return Page();
        }

        await _docs.ResolveExceptionAsync(id, new ExceptionResolution
        {
            DocumentId = id,
            ResolvedBy = reviewedBy,
            NwpSupplierOid = supplier!.Oid,
            NwpSupplierReference = supplier.Reference,
            SupplierMasterName = supplier.Name,
            DocumentType = DocumentType.Invoice,
            InvoiceNumber = extraction.InvoiceNumber?.Trim(),
            InvoiceDate = extraction.InvoiceDate,
            BatchOrReference = extraction.Reference?.Trim(),
            Total = extraction.Total,
            Currency = string.IsNullOrWhiteSpace(extraction.Currency) ? "GBP" : extraction.Currency.Trim().ToUpperInvariant(),
            AssignedQueue = "Invoice Queue",
            MarkReviewed = true,
            WorkflowOwner = document.WorkflowOwner,
            ObservedSupplierName = extraction.SupplierName?.Trim(),
            ResolutionNote = document.ReviewerNote
        }, cancellationToken);

        SuccessMessage = $"{document.Subject} was marked Reviewed.";
        return RedirectToPage();
    }

    public static IReadOnlyList<string> GetQuickApprovalBlockers(CapturedDocument document, bool authoritativeSupplierExists)
    {
        var blockers = new List<string>();
        var extraction = document.Extraction;
        if (extraction is null)
        {
            blockers.Add("there is no live extraction");
            return blockers;
        }

        if (string.IsNullOrWhiteSpace(document.Subject)) blockers.Add("the subject is missing");
        if (string.IsNullOrWhiteSpace(extraction.SupplierName)) blockers.Add("the observed supplier is missing");
        if (string.IsNullOrWhiteSpace(document.NwpSupplierOid)
            || string.IsNullOrWhiteSpace(document.SupplierMasterName)
            || !authoritativeSupplierExists)
            blockers.Add("an authoritative NWP supplier mapping is missing");
        if (string.IsNullOrWhiteSpace(extraction.InvoiceNumber) && string.IsNullOrWhiteSpace(extraction.Reference))
            blockers.Add("the invoice/reference number is missing");
        if (extraction.InvoiceDate is null) blockers.Add("the invoice date is missing");
        if (extraction.Total is null) blockers.Add("the total is missing");
        if (document.ReceivedDate == default) blockers.Add("the received date is missing");
        return blockers;
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Documents = (await _docs.AllAsync(cancellationToken))
            .Where(IsActiveQueueDocument)
            .ToList();

        QuickApprovalBlockers = Documents.ToDictionary(
            document => document.Id,
            document => GetQuickApprovalBlockers(document,
                !string.IsNullOrWhiteSpace(document.NwpSupplierOid)
                && !string.IsNullOrWhiteSpace(document.SupplierMasterName)));
    }

    public static bool IsActiveQueueDocument(CapturedDocument document) =>
        document.DocumentType == DocumentType.Invoice
        && document.Status == ClassificationStatus.ReviewRequired;
}
