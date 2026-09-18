namespace Nwp.InvoiceAutomation.Web.Services;

public interface IGraphDocumentClient
{
    Task<GraphDocumentContent> DownloadFromSharingUrlAsync(string sharingUrl, CancellationToken cancellationToken = default);
    Task<GraphDocumentContent> DownloadDriveItemAsync(string siteId, string driveId, string itemId, string fileName, string? contentType = null, CancellationToken cancellationToken = default);
}

public sealed class GraphDocumentContent : IDisposable
{
    public GraphDocumentContent(Stream stream, string contentType, string fileName)
    {
        Stream = stream;
        ContentType = contentType;
        FileName = fileName;
    }

    public Stream Stream { get; }
    public string ContentType { get; }
    public string FileName { get; }

    public void Dispose() => Stream.Dispose();
}
