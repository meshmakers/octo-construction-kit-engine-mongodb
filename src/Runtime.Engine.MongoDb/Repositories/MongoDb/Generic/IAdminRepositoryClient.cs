using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

/// <summary>
///     Interface of repository client for admin operations.
/// </summary>
public interface IAdminRepositoryClient : IRepositoryClient
{
    Task<IOctoAdminSession> GetAdminSessionAsync();

    IOctoAdminSession GetSystemSession();

    Task CreateRepositoryAsync(string name);

    Task DropRepositoryAsync(string name);

    Task<bool> IsRepositoryExistingAsync(string name);

    Task CreateUser(string authenticationDatabaseName, string userDatabaseName, string user,
        string? password);

    /// <summary>
    ///     Drops a database user. No-op if the user does not exist. Used to roll back a partially
    ///     created tenant (AB#1958).
    /// </summary>
    Task DropUser(string authenticationDatabaseName, string user);
}