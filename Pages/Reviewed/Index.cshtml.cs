using Microsoft.AspNetCore.Mvc.RazorPages;
using Nwp.InvoiceAutomation.Web.Models;
using Nwp.InvoiceAutomation.Web.Services;

namespace Nwp.InvoiceAutomation.Web.Pages.Reviewed;

public sealed class IndexModel : PageModel
{
    private readonly IDocumentStore _docs;

    public IndexModel(IDocumentStore docs) => _docs = docs;

    public IReadOnlyList<CapturedDocument> Documents { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Documents = (await _docs.AllAsync(cancellationToken))
            .Where(IsReviewedDocument)
            .ToList();
    }

    public static bool IsReviewedDocument(CapturedDocument document) =>
        document.DocumentType == DocumentType.Invoice
        && document.Status == ClassificationStatus.Classified
        && document.Extraction?.ReviewStatus == ExtractionReviewStatus.Approved;
}
