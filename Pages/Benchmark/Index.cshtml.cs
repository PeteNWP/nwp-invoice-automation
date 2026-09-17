using Microsoft.AspNetCore.Mvc.RazorPages;
using Nwp.InvoiceAutomation.Web.Models;
using Nwp.InvoiceAutomation.Web.Services;

namespace Nwp.InvoiceAutomation.Web.Pages.Benchmark;

public sealed class IndexModel : PageModel
{
    private readonly BenchmarkStore _benchmark;

    public IndexModel(BenchmarkStore benchmark) => _benchmark = benchmark;

    public IReadOnlyList<BenchmarkHeader> Headers { get; private set; } = Array.Empty<BenchmarkHeader>();

    public int InvoiceCount { get; private set; }
    public int RejectCount { get; private set; }
    public string Source { get; private set; } = "";

    public IReadOnlyList<BenchmarkLine> LinesFor(string invoiceNumber) => _benchmark.LinesFor(invoiceNumber);

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await _benchmark.EnsureLoadedAsync(cancellationToken);
        Headers = _benchmark.Headers();
        InvoiceCount = Headers.Count(h => h.IsInvoice);
        RejectCount = Headers.Count - InvoiceCount;
        Source = _benchmark.Source;
    }
}
