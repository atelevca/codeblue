using System.Text.RegularExpressions;

namespace HealthTech.Jobs
{
    /// <summary>
    /// Приводит присланное клиентом имя файла к безопасному: никаких каталогов, никаких
    /// символов, недопустимых в путях. Кириллица и пробелы сохраняются — они законны.
    /// </summary>
    public static partial class SafeFileName
    {
        private const int MaxLength = 100;
        private const int MaxExtensionLength = 20;

        public static string Sanitize(string? fileName)
        {
            // GetFileName отсекает "../" и "C:\": на выходе остаётся только последний сегмент.
            var name = Path.GetFileName(fileName ?? "").Trim();
            name = Unsafe().Replace(name, "_");

            if (name.Length > MaxLength)
            {
                // Расширение тоже приходит от клиента и само может быть длиннее лимита,
                // поэтому сначала обрезаем его, а потом режем результат целиком: иначе
                // "a." + 200 символов проскакивало мимо лимита и роняло File.Create в 500.
                var extension = Path.GetExtension(name);
                if (extension.Length > MaxExtensionLength)
                {
                    extension = extension[..MaxExtensionLength];
                }
                name = string.Concat(name.AsSpan(0, Math.Max(1, MaxLength - extension.Length)), extension);
                if (name.Length > MaxLength)
                {
                    name = name[..MaxLength];
                }
            }

            return name.Length == 0 || name.All(c => c is '.' or '_') ? "upload" : name;
        }

        [GeneratedRegex(@"[\x00-\x1f<>:""/\\|?*]")]
        private static partial Regex Unsafe();
    }
}
