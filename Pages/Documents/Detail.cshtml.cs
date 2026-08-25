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

    public IActionResult OnGet(Guid id)
    {
        Document = _docs.Get(id);
        return Page();
    }
}
