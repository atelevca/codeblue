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
            _logger.LogInformation("Схема прикладной базы применена из {ScriptPath}", scriptPath);
        }
    }
}
