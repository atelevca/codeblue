using System.Data;
using Microsoft.Data.Sqlite;

namespace HealthTech.Data
{
    public interface IDbConnectionFactory
    {
        /// <summary>Открытое соединение с прикладной базой. Вызывающий обязан его освободить.</summary>
        IDbConnection Create();
    }

    public class SqliteConnectionFactory : IDbConnectionFactory
    {
        private readonly string _connectionString;

        public SqliteConnectionFactory(string databasePath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
            _connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate
                // Shared cache сознательно не включаем: он переносит конкуренцию с файла на
                // внутренние блокировки таблиц и даёт read-uncommitted. WAL + busy_timeout ниже
                // решают ту же задачу и ничего не меняют в семантике транзакций.
            }.ToString();
        }

        public IDbConnection Create()
        {
            var connection = new SqliteConnection(_connectionString);
            connection.Open();
            // WAL: читающие не блокируются пишущим. busy_timeout: вместо мгновенной
            // "database is locked" соединение ждёт освобождения файла.
            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
            pragma.ExecuteNonQuery();
            return connection;
        }
    }
}
