using Microsoft.AspNetCore.Mvc.RazorPages;
using Nwp.InvoiceAutomation.Web.Models;
using Nwp.InvoiceAutomation.Web.Services;

namespace Nwp.InvoiceAutomation.Web.Pages.Suppliers;

public sealed class IndexModel : PageModel
{
    private readonly SupplierProfileStore _suppliers;

    public IndexModel(SupplierProfileStore suppliers) => _suppliers = suppliers;

    public IReadOnlyList<SupplierProfile> Profiles { get; private set; } = Array.Empty<SupplierProfile>();

    public void OnGet() => Profiles = _suppliers.All();
}
