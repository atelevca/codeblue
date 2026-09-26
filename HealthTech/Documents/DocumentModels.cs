using SemanticKernel.Minutes;

namespace HealthTech.Documents;

public sealed record SaveDocumentRequest(QuillDelta Delta);
public sealed record SavedDocument(Guid JobId, string MinutesMarkdown, MinutesVerification Verification, DateTimeOffset SavedAt, QuillDelta Delta);
public sealed record DocumentDownload(byte[] Content, string FileName);
public sealed record SendEmailRequest(IReadOnlyList<string>? To, string? Subject, string? Body);
public sealed record SentEmail(IReadOnlyList<string> To, string Subject, string AttachmentFileName);

/// <summary>Фаза генерации протокола. Нужна только для полосы прогресса: шаг идёт минутами.</summary>
public enum DocumentPhase
{
    ExtractingFacts,
    Generating,
    Verifying
}

public interface IDocumentService
{
    Task<SavedDocument> GetAsync(Guid jobId, CancellationToken ct = default);
    Task<SavedDocument> SaveAsync(Guid jobId, SaveDocumentRequest? request, CancellationToken ct = default);

    /// <summary>Генерация из шага конвейера: без проверки статуса задания.</summary>
    Task<SavedDocument> GenerateAsync(Guid jobId, IProgress<DocumentPhase>? phase = null, CancellationToken ct = default);
    Task<DocumentDownload> DownloadPdfAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>Sends the saved minutes as a PDF attachment through the local SMTP server.</summary>
    Task<SentEmail> SendEmailAsync(Guid jobId, SendEmailRequest? request, CancellationToken ct = default);
}
