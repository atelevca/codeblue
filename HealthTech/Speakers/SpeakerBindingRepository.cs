using Dapper;
using HealthTech.Data;

namespace HealthTech.Speakers
{
    public interface ISpeakerBindingRepository
    {
        Task<IReadOnlyList<SpeakerBinding>> GetAsync(Guid jobId, CancellationToken cancellationToken = default);

        /// <summary>Заменяет весь набор привязок задания: PUT - это целиком новый список, а не добавление.</summary>
        Task ReplaceAsync(Guid jobId, IReadOnlyList<SpeakerBinding> bindings, CancellationToken cancellationToken = default);
    }

    public class SpeakerBindingRepository : ISpeakerBindingRepository
    {
        private readonly IDbConnectionFactory _connections;

        public SpeakerBindingRepository(IDbConnectionFactory connections)
        {
            _connections = connections;
        }

        public async Task<IReadOnlyList<SpeakerBinding>> GetAsync(
            Guid jobId, CancellationToken cancellationToken = default)
        {
            using var connection = _connections.Create();
            var rows = await connection.QueryAsync<(string SpeakerLabel, string PersonId)>(
                "SELECT SpeakerLabel, PersonId FROM SpeakerBindings WHERE JobId = @JobId",
                new { JobId = jobId.ToString() });
            return rows.Select(r => new SpeakerBinding(r.SpeakerLabel, Guid.Parse(r.PersonId))).ToList();
        }

        public async Task ReplaceAsync(
            Guid jobId, IReadOnlyList<SpeakerBinding> bindings, CancellationToken cancellationToken = default)
        {
            // Фабрика отдаёт уже открытое соединение.
            using var connection = _connections.Create();
            // Всё или ничего: половина сохранённых привязок хуже, чем ни одной.
            using var transaction = connection.BeginTransaction();
            await connection.ExecuteAsync(
                "DELETE FROM SpeakerBindings WHERE JobId = @JobId",
                new { JobId = jobId.ToString() }, transaction);
            if (bindings.Count > 0)
            {
                await connection.ExecuteAsync(
                    "INSERT INTO SpeakerBindings (JobId, SpeakerLabel, PersonId) VALUES (@JobId, @SpeakerLabel, @PersonId)",
                    bindings.Select(b => new
                    {
                        JobId = jobId.ToString(),
                        SpeakerLabel = b.Label,
                        PersonId = b.PersonId.ToString()
                    }), transaction);
            }
            transaction.Commit();
        }
    }
}
