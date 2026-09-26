using System.Text.Json;
using HealthTech.Audio;
using HealthTech.Email;
using HealthTech.Jobs;
using HealthTech.Speakers;
using SemanticKernel.Minutes;

namespace HealthTech.Documents;

public sealed class DocumentService(
    IJobRepository jobs, IJobPaths paths, Lazy<IMeetingMinutesGenerator> generator,
    ISpeakerBindingService speakers, ISpeakerBindingRepository bindings, IPersonRepository persons,
    IDocumentPdfRenderer pdf, IEmailSender email, ILogger<DocumentService> logger) : IDocumentService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    // Bounded lock set prevents concurrent saves for the same job without accumulating per-job locks.
    private readonly SemaphoreSlim[] _saveLocks = Enumerable.Range(0, 32).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public async Task<SavedDocument> GetAsync(Guid jobId, CancellationToken ct = default)
    {
        await RequireJobAsync(jobId, ct);
        var path = DocumentPath(jobId);
        if (!File.Exists(path))
            throw new DocumentException(404, "Documentul nu a fost salvat pentru acest job.");

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return await JsonSerializer.DeserializeAsync<SavedDocument>(stream, JsonOptions, ct)
            ?? throw new DocumentException(500, "Documentul salvat nu poate fi citit.");
    }

    public async Task<SavedDocument> SaveAsync(Guid jobId, SaveDocumentRequest? request, CancellationToken ct = default)
    {
        var job = await RequireJobAsync(jobId, ct);
        if (job.Status != JobStatus.Completed)
            throw new DocumentException(409, "Transcrierea jobului nu este finalizată.");
        return await SaveCoreAsync(job, request, null, ct);
    }

    /// <summary>
    /// Вход для шага конвейера. От SaveAsync отличается ровно одним: не требует статуса
    /// Completed. На этом шаге задание ещё Running - в Completed его переводит сам шаг,
    /// после того как протокол сохранён.
    /// </summary>
    public async Task<SavedDocument> GenerateAsync(
        Guid jobId, IProgress<DocumentPhase>? phase = null, CancellationToken ct = default)
    {
        var job = await RequireJobAsync(jobId, ct);
        return await SaveCoreAsync(job, null, phase, ct);
    }

    private async Task<SavedDocument> SaveCoreAsync(
        Job job, SaveDocumentRequest? request, IProgress<DocumentPhase>? phase, CancellationToken ct)
    {
        var jobId = job.Id;
        if (request != null && request.Delta == null)
            throw new DocumentException(400, "Delta este obligatoriu. Omiteți corpul cererii pentru generare.");
        var editedMarkdown = request == null ? null : QuillDocument.ToMarkdown(request.Delta);

        var gate = _saveLocks[(int)((uint)jobId.GetHashCode() % (uint)_saveLocks.Length)];
        await gate.WaitAsync(ct);
        try
        {
            var transcript = await ReadTranscriptAsync(jobId, ct);
            var metadata = await ReadMetadataAsync(job, ct);
            string markdown;
            QuillDelta delta;
            MinutesVerification verification;
            try
            {
                if (request == null)
                {
                    phase?.Report(DocumentPhase.ExtractingFacts);
                    var facts = await generator.Value.ExtractFactsAsync(transcript, metadata, ct);
                    phase?.Report(DocumentPhase.Generating);
                    var generated = await generator.Value.GenerateMinutesAsync(facts, ct);
                    delta = QuillDocument.FromMarkdown(generated);
                    markdown = QuillDocument.ToMarkdown(delta);
                }
                else
                {
                    delta = request.Delta;
                    markdown = editedMarkdown!;
                }
                phase?.Report(DocumentPhase.Verifying);
                verification = await VerifyOrNoteAsync(transcript, markdown, metadata, ct);
            }
            catch (ArgumentException ex)
            {
                throw new DocumentException(422, ex.Message);
            }

            var document = new SavedDocument(jobId, markdown, verification, DateTimeOffset.UtcNow, delta);
            var path = DocumentPath(jobId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(document, JsonOptions), ct);
                ct.ThrowIfCancellationRequested();
                File.Move(temporaryPath, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            return document;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Сверка - контроль качества, а не условие сохранения. Она требует от модели дословных
    /// цитат из транскрипта, и на искажённом распознавании 7B в четырёхбитном кванте их не
    /// выдаёт: цитата перефразируется или теряет диакритику и не находится подстрокой.
    /// Ронять запрос из-за этого значит выбросить уже готовый документ после десяти минут
    /// счёта. Документ сохраняется как есть, а несостоявшаяся сверка помечается явно.
    /// </summary>
    private async Task<MinutesVerification> VerifyOrNoteAsync(
        string transcript, string markdown, MeetingMetadata? metadata, CancellationToken ct)
    {
        try
        {
            return await generator.Value.VerifyMinutesAsync(transcript, markdown, metadata, ct);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or ArgumentException or DocumentException))
        {
            logger.LogWarning(ex, "Сверка протокола не удалась; документ сохранён без неё");
            return new MinutesVerification
            {
                Completed = false,
                Summary = "Verificarea automată nu a putut fi finalizată. " +
                          "Documentul este salvat așa cum a fost generat și necesită revizuire manuală.",
                Findings = []
            };
        }
    }

    public async Task<DocumentDownload> DownloadPdfAsync(Guid jobId, CancellationToken ct = default)
    {
        var document = await GetAsync(jobId, ct);
        ct.ThrowIfCancellationRequested();
        var content = pdf.Render(document);
        ct.ThrowIfCancellationRequested();
        return new DocumentDownload(content, $"proces-verbal-{jobId}.pdf");
    }

    public async Task<SentEmail> SendEmailAsync(Guid jobId, SendEmailRequest? request, CancellationToken ct = default)
    {
        var to = (request?.To ?? [])
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (to.Count == 0)
            throw new DocumentException(400, "Adăugați cel puțin un destinatar.");
        if (to.Count > MaxRecipients)
            throw new DocumentException(400, $"Cel mult {MaxRecipients} destinatari.");
        var invalid = to.FirstOrDefault(x => !IsPlainAddress(x));
        if (invalid != null)
            throw new DocumentException(400, $"„{invalid}” nu este o adresă de e-mail validă.");

        var subject = string.IsNullOrWhiteSpace(request?.Subject) ? "Proces-verbal" : request.Subject.Trim();
        var body = string.IsNullOrWhiteSpace(request?.Body)
            ? "Procesul-verbal al ședinței este atașat în format PDF."
            : request.Body;

        var pdf = await DownloadPdfAsync(jobId, ct);
        await email.SendAsync(to, subject, body, new EmailAttachment(pdf.FileName, pdf.Content, "application/pdf"), ct);
        return new SentEmail(to, subject, pdf.FileName);
    }

    private const int MaxRecipients = 20;

    // Bare "user@host" only: no display names, lists or header tricks from the request.
    private static bool IsPlainAddress(string value) =>
        MimeKit.MailboxAddress.TryParse(value, out var mailbox) && mailbox.Address == value && value.Contains('@');

    private async Task<Job> RequireJobAsync(Guid jobId, CancellationToken ct) =>
        await jobs.GetAsync(jobId, ct) ?? throw new DocumentException(404, "Jobul nu a fost găsit.");

    private string DocumentPath(Guid jobId) => Path.Combine(paths.TranscriptsDirectory(jobId), "minutes.document.json");

    /// <summary>
    /// The dialogue with the bound doctors' names in place of "Speaker N" - the same text
    /// GET /jobs/{id}/transcript returns. Before any binding the labels stay, so the minutes
    /// generated by the pipeline name nobody; regenerating after binding names the speakers.
    /// The verification quotes refer to this text.
    /// </summary>
    private async Task<string> ReadTranscriptAsync(Guid jobId, CancellationToken ct)
    {
        NamedTranscript transcript;
        try
        {
            transcript = await speakers.GetTranscriptAsync(jobId, ct);
        }
        catch (AudioProcessingException ex) when (ex.Error == AudioProcessingError.InputNotFound)
        {
            throw new DocumentException(409, "Nu există un transcript final pentru acest job.");
        }
        catch (AudioProcessingException ex) when (ex.Error == AudioProcessingError.InvalidTranscript)
        {
            throw new DocumentException(422, "Transcriptul final nu este un JSON valid.");
        }
        if (string.IsNullOrWhiteSpace(transcript.Text))
            throw new DocumentException(422, "Transcriptul final nu conține text.");
        return transcript.Text;
    }

    /// <summary>
    /// What the application knows besides the transcript: the record title and the doctors bound
    /// to the speakers, with their specialty. Null when neither exists, so the prompts see no
    /// metadata rather than an empty object.
    /// </summary>
    private async Task<MeetingMetadata?> ReadMetadataAsync(Job job, CancellationToken ct)
    {
        var bound = await bindings.GetAsync(job.Id, ct);
        var people = bound.Count == 0
            ? new Dictionary<Guid, Person>()
            : (await persons.ListAsync(ct)).ToDictionary(p => p.Id);
        var participants = bound
            .Where(b => people.ContainsKey(b.PersonId))
            .Select(b => people[b.PersonId])
            .DistinctBy(p => p.Id)
            .Select(p => new MeetingParticipant { Name = p.FullName, Role = p.Specialty })
            .ToArray();
        var title = string.IsNullOrWhiteSpace(job.Title) ? null : job.Title.Trim();
        return title == null && participants.Length == 0
            ? null
            : new MeetingMetadata { Title = title, Participants = participants };
    }
}
