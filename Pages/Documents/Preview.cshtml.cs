using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Nwp.InvoiceAutomation.Web.Services;

namespace Nwp.InvoiceAutomation.Web.Pages.Documents;

public sealed class PreviewModel : PageModel
{
    private readonly IDocumentStore _docs;
    private readonly IGraphDocumentClient _graphClient;

    public PreviewModel(IDocumentStore docs, IGraphDocumentClient graphClient)
    {
        _docs = docs;
        _graphClient = graphClient;
    }

    public async Task<IActionResult> OnGet(Guid id, CancellationToken cancellationToken)
    {
        var document = _docs.Get(id);
        if (document is null || string.IsNullOrWhiteSpace(document.AttachmentPreviewUrl))
        {
            return NotFound("Document preview is not available.");
        }

        if (document.AttachmentPreviewUrl.StartsWith("/", StringComparison.Ordinal))
        {
            return Redirect(document.AttachmentPreviewUrl);
        }

        try
        {
            var content = await _graphClient.DownloadFromSharingUrlAsync(document.AttachmentPreviewUrl, cancellationToken);
            Response.Headers.CacheControl = "private, max-age=300";
            Response.Headers.ContentDisposition = $"inline; filename=\"{content.FileName}\"";
            return new FileStreamResult(content.Stream, content.ContentType)
            {
                EnableRangeProcessing = true
            };
        }
        catch (Exception ex)
        {
            Response.StatusCode = StatusCodes.Status502BadGateway;
            return Content($"Document preview failed. {ex.Message}", "text/plain");
        }
    }
}
