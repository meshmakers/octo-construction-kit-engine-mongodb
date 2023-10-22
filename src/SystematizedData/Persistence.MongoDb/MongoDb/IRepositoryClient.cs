using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

namespace Meshmakers.Octo.SystematizedData.Persistence.MongoDb;

public interface IRepositoryClient
{
    Task<IOctoSystemSession> GetSessionAsync();

    IOctoSession StartSession();
    
    Task CreateRepositoryAsync(string name);

    Task DropRepositoryAsync(string name);

    IRepository GetRepository(string name);

    Task<bool> IsRepositoryExistingAsync(string name);

    Task CreateUser(string authenticationDatabaseName, string databaseName, string user,
        string? password);
}
