using Microsoft.AspNetCore.Mvc.RazorPages;
using Nwp.InvoiceAutomation.Web.Models;
using Nwp.InvoiceAutomation.Web.Services;

namespace Nwp.InvoiceAutomation.Web.Pages;

public sealed class IndexModel : PageModel
{
    private readonly IDocumentStore _docs;
    private readonly SupplierProfileStore _suppliers;

    public IndexModel(IDocumentStore docs, SupplierProfileStore suppliers)
    {
        _docs = docs;
        _suppliers = suppliers;
    }

    public int Total { get; private set; }
    public int Invoices { get; private set; }
    public int Statements { get; private set; }
    public int Exceptions { get; private set; }
    public int Ignored { get; private set; }
    public int Suppliers { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var all = await _docs.AllAsync(cancellationToken);
        Total = all.Count;
        Invoices = all.Count(d => d.DocumentType == DocumentType.Invoice && d.Status == ClassificationStatus.Classified);
        Statements = all.Count(d => d.DocumentType == DocumentType.Statement);
        Exceptions = all.Count(d => d.Status == ClassificationStatus.Exception);
        Ignored = all.Count(d => d.Status == ClassificationStatus.Ignored);
        Suppliers = _suppliers.All().Count;
    }
}
