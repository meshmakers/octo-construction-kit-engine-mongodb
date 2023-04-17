using System.Threading.Tasks;
using Meshmakers.Octo.Backend.Persistence.DataAccess;

namespace Meshmakers.Octo.Backend.Persistence.MongoDb;

public interface IRepositoryClient
{
    Task CreateRepositoryAsync(string name);

    Task DropRepositoryAsync(string name);

    IRepository GetRepository(string name);

    Task<bool> IsRepositoryExistingAsync(string name);

    Task CreateUser(IOctoSession session, string authenticationDatabaseName, string databaseName, string user,
        string password);
}
