using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Nwp.InvoiceAutomation.Web.Models;
using Nwp.InvoiceAutomation.Web.Services;

namespace Nwp.InvoiceAutomation.Web.Pages.Documents;

public sealed class DetailModel : PageModel
{
    private readonly IDocumentStore _docs;

    public DetailModel(IDocumentStore docs) => _docs = docs;

    public CapturedDocument? Document { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken cancellationToken)
    {
        Document = await _docs.GetAsync(id, cancellationToken);
        return Page();
    }
}
