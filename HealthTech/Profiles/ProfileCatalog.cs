using HealthTech.Audio;
using Microsoft.Extensions.Options;
using SemanticKernel.MedicalCorrection;

namespace HealthTech.Profiles
{
    public interface IProfileCatalog
    {
        IReadOnlyList<RecordProfile> All { get; }

        /// <summary>Профиль по ключу. Неизвестный ключ — <see cref="AudioProcessingException"/> с перечнем доступных.</summary>
        RecordProfile Get(string key);
    }

    /// <summary>
    /// Синглтон: разбирает промпты и глоссарии один раз при старте. medical_glossary.txt —
    /// 850 строк, разбирать его на каждый запрос незачем.
    /// </summary>
    public class ProfileCatalog : IProfileCatalog
    {
        private const string MedicalGlossaryHeading =
            "Reference medical terms (use only to recognize misheard words):";
        private const string SpeechGlossaryHeading =
            "Normal Moldovan mixed speech (NOT errors, keep these words exactly as written; use only to understand the sentence):";
        private const string PhoneticHeading =
            "Known ASR mishearings (left = what the recognizer produces, right = the correct form):";
        private const string Icd10Heading =
            "Reference ICD-10 diagnoses (code | name | chapter; names are written WITHOUT diacritics - use them only " +
            "to recognize misheard diagnosis names, never remove diacritics from the text; do not insert codes):";

        private readonly Dictionary<string, RecordProfile> _profiles;

        public ProfileCatalog(IOptions<ProfileOptions> options, ILogger<ProfileCatalog> logger)
        {
            _profiles = new Dictionary<string, RecordProfile>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, definition) in options.Value.Items)
            {
                var systemPromptPath = Path.Combine(AppContext.BaseDirectory, definition.SystemPromptFile);
                if (!File.Exists(systemPromptPath))
                {
                    throw new InvalidOperationException(
                        $"Профиль '{key}': не найден системный промпт '{systemPromptPath}'.");
                }

                var glossaries = new List<Glossary>();
                foreach (var file in definition.Glossaries)
                {
                    var path = Path.Combine(AppContext.BaseDirectory, "Glossary", file);
                    if (!File.Exists(path))
                    {
                        throw new InvalidOperationException($"Профиль '{key}': не найден глоссарий '{path}'.");
                    }
                    var glossary = IsIcd10(file)
                        ? Icd10Glossary.Load(path, Icd10Heading)
                        : Glossary.Load(path, HeadingFor(file));
                    glossaries.Add(glossary);
                    logger.LogInformation("Профиль {Profile}: глоссарий {File} - {Count} строк", key, file, glossary.Count);
                }

                _profiles[key] = new RecordProfile(key, definition.DisplayName, definition.WhisperPrompt,
                    new RecordProfileContent(File.ReadAllText(systemPromptPath), glossaries));
            }

            if (_profiles.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Секция '{ProfileOptions.SectionName}' пуста: нужен хотя бы один профиль записи.");
            }

            logger.LogInformation("Загружено профилей записей: {Count} ({Keys})",
                _profiles.Count, string.Join(", ", _profiles.Keys));
        }

        public IReadOnlyList<RecordProfile> All => _profiles.Values.ToList();

        public RecordProfile Get(string key) =>
            _profiles.TryGetValue(key ?? "", out var profile)
                ? profile
                : throw new AudioProcessingException(AudioProcessingError.UnknownProfile,
                    $"Неизвестный тип записи '{key}'. Доступные: {string.Join(", ", _profiles.Keys)}.");

        // diagnostics.csv - выгрузка МКБ-10 (три файла, разделитель ';'); subclasses.csv и classes.csv
        // читаются из той же папки и в список глоссариев профиля не попадают.
        private static bool IsIcd10(string fileName) =>
            fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);

        // Списки подключаются одинаково, но означают разное, поэтому и заголовки разные:
        // термины - что искать, нормальная речь - что не трогать, фонетика - что чем заменять.
        private static string HeadingFor(string fileName) =>
            fileName.Contains("phonetic", StringComparison.OrdinalIgnoreCase) ? PhoneticHeading
            : fileName.Contains("moldova_speech", StringComparison.OrdinalIgnoreCase) ? SpeechGlossaryHeading
            : MedicalGlossaryHeading;
    }
}
