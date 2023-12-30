using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

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