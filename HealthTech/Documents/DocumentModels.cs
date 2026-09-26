using SemanticKernel.Minutes;

namespace HealthTech.Documents;

public sealed record SaveDocumentRequest(QuillDelta Delta);
public sealed record SavedDocument(Guid JobId, string MinutesMarkdown, MinutesVerification Verification, DateTimeOffset SavedAt, QuillDelta Delta);
public sealed record DocumentDownload(byte[] Content, string FileName);

/// <summary>Фаза генерации протокола. Нужна только для полосы прогресса: шаг идёт минутами.</summary>
public enum DocumentPhase
{
    ExtractingFacts,
    Verifying
}

public interface IDocumentService
{
    Task<SavedDocument> GetAsync(Guid jobId, CancellationToken ct = default);
    Task<SavedDocument> SaveAsync(Guid jobId, SaveDocumentRequest? request, CancellationToken ct = default);

    /// <summary>
    /// Генерация из шага конвейера: без проверки статуса задания и без сверки. Документ
    /// сохраняется с пометкой "сверка в процессе" и ставится в очередь фоновой сверки.
    /// </summary>
    Task<SavedDocument> GenerateAsync(Guid jobId, IProgress<DocumentPhase>? phase = null, CancellationToken ct = default);

    /// <summary>
    /// Фоновая сверка сохранённого документа. Ничего не делает, если сверка уже не ожидается
    /// (документ пересохранили или сверили раньше); результат записывается, только если документ
    /// не менялся, пока шла модель.
    /// </summary>
    Task VerifyPendingAsync(Guid jobId, CancellationToken ct = default);

    /// <summary>Задания, чей сохранённый протокол всё ещё ждёт сверки (для подбора после перезапуска).</summary>
    Task<IReadOnlyList<Guid>> FindPendingVerificationsAsync(CancellationToken ct = default);

    Task<DocumentDownload> DownloadPdfAsync(Guid jobId, CancellationToken ct = default);
}
