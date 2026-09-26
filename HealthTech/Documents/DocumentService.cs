using System.Text.Json;
using HealthTech.Jobs;
using HealthTech.Transcription;
using SemanticKernel.Minutes;

namespace HealthTech.Documents;

public sealed class DocumentService(
    IJobRepository jobs, IJobPaths paths, Lazy<IMeetingMinutesGenerator> generator,
    IDocumentPdfRenderer pdf) : IDocumentService
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
        if (request != null && request.Delta == null)
            throw new DocumentException(400, "Delta este obligatoriu. Omiteți corpul cererii pentru generare.");
        var editedMarkdown = request == null ? null : QuillDocument.ToMarkdown(request.Delta);

        var gate = _saveLocks[(int)((uint)jobId.GetHashCode() % (uint)_saveLocks.Length)];
        await gate.WaitAsync(ct);
        try
        {
            var transcript = await ReadTranscriptAsync(jobId, ct);
            string markdown;
            QuillDelta delta;
            MinutesVerification verification;
            try
            {
                if (request == null)
                {
                    var facts = await generator.Value.ExtractFactsAsync(transcript, ct);
                    var generated = await generator.Value.GenerateMinutesAsync(facts, ct);
                    delta = QuillDocument.FromMarkdown(generated);
                    markdown = QuillDocument.ToMarkdown(delta);
                }
                else
                {
                    delta = request.Delta;
                    markdown = editedMarkdown!;
                }
                verification = await generator.Value.VerifyMinutesAsync(transcript, markdown, ct);
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

    public async Task<DocumentDownload> DownloadPdfAsync(Guid jobId, CancellationToken ct = default)
    {
        var document = await GetAsync(jobId, ct);
        ct.ThrowIfCancellationRequested();
        var content = pdf.Render(document);
        ct.ThrowIfCancellationRequested();
        return new DocumentDownload(content, $"proces-verbal-{jobId}.pdf");
    }

    private async Task<Job> RequireJobAsync(Guid jobId, CancellationToken ct) =>
        await jobs.GetAsync(jobId, ct) ?? throw new DocumentException(404, "Jobul nu a fost găsit.");

    private string DocumentPath(Guid jobId) => Path.Combine(paths.TranscriptsDirectory(jobId), "minutes.document.json");

    private async Task<string> ReadTranscriptAsync(Guid jobId, CancellationToken ct)
    {
        var directory = paths.TranscriptsDirectory(jobId);
        var files = Directory.Exists(directory) ? Directory.GetFiles(directory, "*.speakers.json") : [];
        if (files.Length != 1)
            throw new DocumentException(409, "Nu există un transcript final unic pentru acest job.");
        var source = await File.ReadAllTextAsync(files[0], ct);
        SpeakerTranscriptResult? transcript;
        try
        {
            transcript = JsonSerializer.Deserialize<SpeakerTranscriptResult>(source, JsonOptions);
        }
        catch (JsonException)
        {
            throw new DocumentException(422, "Transcriptul final nu este un JSON valid.");
        }
        if (string.IsNullOrWhiteSpace(transcript?.Text))
            throw new DocumentException(422, "Transcriptul final nu conține text.");
        return transcript.Text;
    }
}
