using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

/// <summary>
/// Interface of repository client for admin operations.
/// </summary>
public interface IAdminRepositoryClient : IRepositoryClient
{
    Task<IOctoSystemSession> GetSystemSessionAsync();

    IOctoSystemSession GetSystemSession();
    
    Task CreateRepositoryAsync(string name);

    Task DropRepositoryAsync(string name);
    
    Task<bool> IsRepositoryExistingAsync(string name);
    
    Task CreateUser(string authenticationDatabaseName, string databaseName, string user,
        string? password);
}