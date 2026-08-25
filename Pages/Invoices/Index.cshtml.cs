using Microsoft.AspNetCore.Mvc.RazorPages;
using Nwp.InvoiceAutomation.Web.Models;
using Nwp.InvoiceAutomation.Web.Services;

namespace Nwp.InvoiceAutomation.Web.Pages.Invoices;

public sealed class IndexModel : PageModel
{
    private readonly IDocumentStore _docs;

    public IndexModel(IDocumentStore docs) => _docs = docs;

    public IReadOnlyList<CapturedDocument> Documents { get; private set; } = Array.Empty<CapturedDocument>();

    public void OnGet() => Documents = _docs.All()
        .Where(d => d.DocumentType == DocumentType.Invoice && d.Status == ClassificationStatus.Classified)
        .ToList();
}
