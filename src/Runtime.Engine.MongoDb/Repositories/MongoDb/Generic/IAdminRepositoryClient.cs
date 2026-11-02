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

    /// <summary>
    ///     Lists all collection names of the given database. The database name is matched
    ///     case-insensitively, like <see cref="IsRepositoryExistingAsync" />; when several stored
    ///     databases match, the union of their collection names is returned. An empty list means the
    ///     database does not exist. Used to classify an infrastructure-only system database shell
    ///     during bootstrap (AB#4854).
    /// </summary>
    Task<IReadOnlyList<string>> ListCollectionNamesAsync(string databaseName);

    Task CreateUser(string authenticationDatabaseName, string userDatabaseName, string user,
        string? password);

    /// <summary>
    ///     Idempotent <see cref="CreateUser" /> that reports whether it had to create the user (AB#8177):
    ///     <c>true</c> = the user was missing and has been created, <c>false</c> = it already existed.
    /// </summary>
    Task<bool> EnsureUserAsync(string authenticationDatabaseName, string userDatabaseName, string user,
        string? password);

    /// <summary>
    ///     Drops a database user. No-op if the user does not exist. Used to roll back a partially
    ///     created tenant (AB#1958).
    /// </summary>
    Task DropUser(string authenticationDatabaseName, string user);
}