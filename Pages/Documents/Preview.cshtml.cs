using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Nwp.InvoiceAutomation.Web.Services;

namespace Nwp.InvoiceAutomation.Web.Pages.Documents;

public sealed class PreviewModel : PageModel
{
    private readonly IDocumentStore _docs;
    private readonly IGraphDocumentClient _graphClient;
    private readonly ILogger<PreviewModel> _logger;

    public PreviewModel(IDocumentStore docs, IGraphDocumentClient graphClient, ILogger<PreviewModel> logger)
    {
        _docs = docs;
        _graphClient = graphClient;
        _logger = logger;
    }

    public async Task<IActionResult> OnGet(Guid id, CancellationToken cancellationToken)
    {
        var document = await _docs.GetAsync(id, cancellationToken);
        if (document is null)
        {
            return NotFound("Document preview is not available.");
        }

        if (!string.IsNullOrWhiteSpace(document.GraphSiteId)
            && !string.IsNullOrWhiteSpace(document.GraphDriveId)
            && !string.IsNullOrWhiteSpace(document.GraphItemId))
        {
            try
            {
                var content = await _graphClient.DownloadDriveItemAsync(
                    document.GraphSiteId,
                    document.GraphDriveId,
                    document.GraphItemId,
                    document.AttachmentName,
                    document.AttachmentMime,
                    cancellationToken);
                Response.Headers.CacheControl = "private, max-age=300";
                Response.Headers.ContentDisposition = $"inline; filename=\"{content.FileName}\"";
                return new FileStreamResult(content.Stream, content.ContentType)
                {
                    EnableRangeProcessing = true
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Graph preview failed for document {DocumentId}.", id);
                Response.StatusCode = StatusCodes.Status502BadGateway;
                return Content("Document preview failed. Check the application log for details.", "text/plain");
            }
        }

        if (string.IsNullOrWhiteSpace(document.AttachmentPreviewUrl))
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
            _logger.LogWarning(ex, "Graph preview failed for document {DocumentId}.", id);
            Response.StatusCode = StatusCodes.Status502BadGateway;
            return Content("Document preview failed. Check the application log for details.", "text/plain");
        }
    }
}
