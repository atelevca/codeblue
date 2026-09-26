using System.Data;
using Dapper;

namespace HealthTech.Data
{
    /// <summary>Прогоняет schema.sql при старте. Скрипт идемпотентен.</summary>
    public class DatabaseInitializer
    {
        private readonly IDbConnectionFactory _connections;
        private readonly ILogger<DatabaseInitializer> _logger;

        public DatabaseInitializer(IDbConnectionFactory connections, ILogger<DatabaseInitializer> logger)
        {
            _connections = connections;
            _logger = logger;
        }

        public void Initialize()
        {
            var scriptPath = Path.Combine(AppContext.BaseDirectory, "Data", "schema.sql");
            if (!File.Exists(scriptPath))
            {
                throw new InvalidOperationException($"Не найден скрипт схемы '{scriptPath}'.");
            }

            using var connection = _connections.Create();
            connection.Execute(File.ReadAllText(scriptPath));
            EnsureJobColumns(connection);
            _logger.LogInformation("Схема прикладной базы применена из {ScriptPath}", scriptPath);
        }

        // CREATE TABLE IF NOT EXISTS на существующей базе не делает ничего, а ALTER TABLE ADD COLUMN
        // не идемпотентен и на втором старте упал бы. Поэтому сверяем фактический набор столбцов и
        // досыпаем недостающие: в базе разработчика лежат уже обработанные задания, сносить её нельзя.
        private void EnsureJobColumns(IDbConnection connection)
        {
            var existing = connection.Query<string>("SELECT name FROM pragma_table_info('Jobs')")
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            (string Name, string Definition)[] required =
            [
                ("SizeBytes",     "INTEGER NOT NULL DEFAULT 0"),
                ("Format",        "TEXT NULL"),
                ("DurationSec",   "REAL NULL"),
                ("Title",         "TEXT NULL"),
                ("SpeakersCount", "INTEGER NULL")
            ];

            foreach (var (name, definition) in required)
            {
                if (existing.Contains(name))
                {
                    continue;
                }

                connection.Execute($"ALTER TABLE Jobs ADD COLUMN {name} {definition}");
                _logger.LogInformation("В таблицу Jobs добавлен столбец {Column}", name);
            }
        }
    }
}
