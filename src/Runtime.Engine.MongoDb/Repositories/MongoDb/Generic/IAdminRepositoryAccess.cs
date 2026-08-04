namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

internal interface IAdminRepositoryAccess
{
    IAdminRepositoryClient GetRepositoryClient(string databaseName);

    /// <summary>
    /// Drops the cached client for <paramref name="databaseName"/> and disposes it, closing its connection
    /// pool. The next call builds a fresh client that authenticates anew — required after the database's
    /// user has been dropped and re-created, because the driver never re-authenticates an already-open
    /// connection (AB#4690). No-op when nothing is cached.
    /// </summary>
    void Invalidate(string databaseName);
}