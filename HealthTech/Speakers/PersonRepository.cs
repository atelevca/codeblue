using Dapper;
using HealthTech.Data;

namespace HealthTech.Speakers
{
    public interface IPersonRepository
    {
        Task<IReadOnlyList<Person>> ListAsync(CancellationToken cancellationToken = default);
        Task<IReadOnlyList<Guid>> ExistingIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default);
    }

    public class PersonRepository : IPersonRepository
    {
        private readonly IDbConnectionFactory _connections;

        public PersonRepository(IDbConnectionFactory connections)
        {
            _connections = connections;
        }

        public async Task<IReadOnlyList<Person>> ListAsync(CancellationToken cancellationToken = default)
        {
            using var connection = _connections.Create();
            var rows = await connection.QueryAsync<(string Id, string FullName, string? Specialty)>(
                "SELECT Id, FullName, Specialty FROM Persons ORDER BY FullName");
            return rows.Select(r => new Person(Guid.Parse(r.Id), r.FullName, r.Specialty)).ToList();
        }

        public async Task<IReadOnlyList<Guid>> ExistingIdsAsync(
            IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default)
        {
            if (ids.Count == 0)
            {
                return [];
            }

            using var connection = _connections.Create();
            var found = await connection.QueryAsync<string>(
                "SELECT Id FROM Persons WHERE Id IN @Ids",
                new { Ids = ids.Select(i => i.ToString()).ToArray() });
            return found.Select(Guid.Parse).ToList();
        }
    }
}
