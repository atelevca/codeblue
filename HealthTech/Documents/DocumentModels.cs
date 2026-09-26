using SemanticKernel.Minutes;

namespace HealthTech.Documents;

public sealed record SaveDocumentRequest(QuillDelta Delta);
public sealed record SavedDocument(Guid JobId, string MinutesMarkdown, MinutesVerification Verification, DateTimeOffset SavedAt, QuillDelta Delta);
public sealed record DocumentDownload(byte[] Content, string FileName);
public sealed record SendEmailRequest(IReadOnlyList<string>? To, string? Subject, string? Body);
public sealed record SentEmail(IReadOnlyList<string> To, string Subject, string AttachmentFileName);

/// <summary>
/// Фаза генерации протокола. Нужна только для полосы прогресса: шаг идёт минутами. Сам документ
/// рендерится из фактов в коде мгновенно, поэтому отдельной фазы у него нет.
/// </summary>
public enum DocumentPhase
{
    ExtractingFacts,
    Consolidating,
    Verifying
}

/// <summary>Фаза и номер фрагмента: длинный транскрипт извлекается и сверяется по окнам.</summary>
public sealed record DocumentProgress(DocumentPhase Phase, int Current = 1, int Total = 1);

public interface IDocumentService
{
    Task<SavedDocument> GetAsync(Guid jobId, CancellationToken ct = default);
    Task<SavedDocument> SaveAsync(Guid jobId, SaveDocumentRequest? request, CancellationToken ct = default);

    /// <summary>
    /// Генерация из шага конвейера: без проверки статуса задания и без сверки. Документ
    /// сохраняется с пометкой "сверка в процессе" и ставится в очередь фоновой сверки.
    /// </summary>
    Task<SavedDocument> GenerateAsync(Guid jobId, IProgress<DocumentProgress>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// Фоновая сверка сохранённого документа. Ничего не делает, если сверка уже не ожидается
    /// (документ пересохранили или сверили раньше); результат записывается, только если документ
    /// не менялся, пока шла модель.
    /// </summary>
    Task VerifyPendingAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>Задания, чей сохранённый протокол всё ещё ждёт сверки (для подбора после перезапуска).</summary>
    Task<IReadOnlyList<Guid>> FindPendingVerificationsAsync(CancellationToken ct = default);

    Task<DocumentDownload> DownloadPdfAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>Sends the saved minutes as a PDF attachment through the local SMTP server.</summary>
    Task<SentEmail> SendEmailAsync(Guid jobId, SendEmailRequest? request, CancellationToken ct = default);
}
