namespace HealthTech.Profiles
{
    public class ProfileOptions
    {
        public const string SectionName = "Profiles";

        public Dictionary<string, ProfileDefinition> Items { get; set; } = [];
    }

    public class ProfileDefinition
    {
        public string DisplayName { get; set; } = "";

        /// <summary>
        /// initial_prompt для Whisper. whisper.cpp обрезает его примерно на 224 токенах,
        /// поэтому это направляющая фраза, а не словарь.
        /// </summary>
        public string WhisperPrompt { get; set; } = "";

        /// <summary>Путь относительно каталога вывода, например "Prompts/medical_correction.system.txt".</summary>
        public string SystemPromptFile { get; set; } = "";

        /// <summary>Имена файлов в каталоге Glossary/.</summary>
        public List<string> Glossaries { get; set; } = [];
    }
}
