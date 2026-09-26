using System.Text.Json;
using HealthTech.Audio;
using HealthTech.Jobs;
using HealthTech.Transcription;

namespace HealthTech.Speakers
{
    public interface ISpeakerBindingService
    {
        Task<IReadOnlyList<Person>> ListPersonsAsync(CancellationToken cancellationToken = default);
        Task<NamedTranscript> BindAsync(Guid jobId, IReadOnlyList<SpeakerBinding> bindings, CancellationToken cancellationToken = default);
        Task<NamedTranscript> GetTranscriptAsync(Guid jobId, CancellationToken cancellationToken = default);
    }

    public class SpeakerBindingService : ISpeakerBindingService
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        private readonly IPersonRepository _persons;
        private readonly ISpeakerBindingRepository _bindings;
        private readonly IJobService _jobs;

        public SpeakerBindingService(
            IPersonRepository persons, ISpeakerBindingRepository bindings, IJobService jobs)
        {
            _persons = persons;
            _bindings = bindings;
            _jobs = jobs;
        }

        public Task<IReadOnlyList<Person>> ListPersonsAsync(CancellationToken cancellationToken = default) =>
            _persons.ListAsync(cancellationToken);

        public async Task<NamedTranscript> BindAsync(
            Guid jobId, IReadOnlyList<SpeakerBinding> bindings, CancellationToken cancellationToken = default)
        {
            var result = await ReadResultAsync(jobId, cancellationToken);

            // Метка не из этой записи - почти всегда опечатка в UI, и молча принять её значит
            // показать человеку, что привязка сохранена, а в диалоге ничего не изменится.
            var known = result.Speakers.ToHashSet(StringComparer.Ordinal);
            var unknownLabels = bindings.Select(b => b.Label).Where(l => !known.Contains(l)).Distinct().ToList();
            if (unknownLabels.Count > 0)
            {
                throw new AudioProcessingException(AudioProcessingError.InvalidRequest,
                    $"В записи нет говорящих: {string.Join(", ", unknownLabels)}. Доступные: {string.Join(", ", result.Speakers)}.");
            }

            var duplicates = bindings.GroupBy(b => b.Label, StringComparer.Ordinal)
                .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (duplicates.Count > 0)
            {
                throw new AudioProcessingException(AudioProcessingError.InvalidRequest,
                    $"Метка указана дважды: {string.Join(", ", duplicates)}.");
            }

            var requested = bindings.Select(b => b.PersonId).Distinct().ToList();
            var existing = (await _persons.ExistingIdsAsync(requested, cancellationToken)).ToHashSet();
            var missing = requested.Where(id => !existing.Contains(id)).ToList();
            if (missing.Count > 0)
            {
                throw new AudioProcessingException(AudioProcessingError.InvalidRequest,
                    $"В справочнике нет врачей: {string.Join(", ", missing)}.");
            }

            await _bindings.ReplaceAsync(jobId, bindings, cancellationToken);
            return await BuildAsync(jobId, result, cancellationToken);
        }

        public async Task<NamedTranscript> GetTranscriptAsync(
            Guid jobId, CancellationToken cancellationToken = default)
        {
            var result = await ReadResultAsync(jobId, cancellationToken);
            return await BuildAsync(jobId, result, cancellationToken);
        }

        private async Task<SpeakerTranscriptResult> ReadResultAsync(Guid jobId, CancellationToken cancellationToken)
        {
            var json = await _jobs.GetResultAsync(jobId, cancellationToken)
                ?? throw new AudioProcessingException(AudioProcessingError.InputNotFound,
                    $"У записи {jobId} нет готового результата: она ещё обрабатывается, упала или не существует.");
            return JsonSerializer.Deserialize<SpeakerTranscriptResult>(json, Json)
                ?? throw new AudioProcessingException(AudioProcessingError.InvalidTranscript,
                    $"Результат записи {jobId} не читается.");
        }

        // Имена подставляются на лету: файлы транскриптов не переписываются, поэтому привязку
        // можно менять сколько угодно раз, не перезапуская обработку.
        private async Task<NamedTranscript> BuildAsync(
            Guid jobId, SpeakerTranscriptResult result, CancellationToken cancellationToken)
        {
            var saved = await _bindings.GetAsync(jobId, cancellationToken);
            var people = (await _persons.ListAsync(cancellationToken)).ToDictionary(p => p.Id);
            var names = saved
                .Where(b => people.ContainsKey(b.PersonId))
                .ToDictionary(b => b.Label, b => people[b.PersonId].FullName, StringComparer.Ordinal);

            var turns = result.Turns
                .Select(t => new NamedTurn(t.Speaker,
                    names.TryGetValue(t.Speaker, out var name) ? name : t.Speaker,
                    t.StartTime, t.EndTime, t.Text))
                .ToList();

            return new NamedTranscript(turns, TranscriptDialogue.Format(turns.Select(t => (t.DisplayName, t.Text))));
        }
    }
}
