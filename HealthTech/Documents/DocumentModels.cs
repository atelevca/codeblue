using SemanticKernel.Minutes;

namespace HealthTech.Documents;

public sealed record SaveDocumentRequest(QuillDelta Delta);
public sealed record SavedDocument(Guid JobId, string MinutesMarkdown, MinutesVerification Verification, DateTimeOffset SavedAt, QuillDelta Delta);
public sealed record DocumentDownload(byte[] Content, string FileName);

public interface IDocumentService
{
    Task<SavedDocument> GetAsync(Guid jobId, CancellationToken ct = default);
    Task<SavedDocument> SaveAsync(Guid jobId, SaveDocumentRequest? request, CancellationToken ct = default);
    Task<DocumentDownload> DownloadPdfAsync(Guid jobId, CancellationToken ct = default);
}
