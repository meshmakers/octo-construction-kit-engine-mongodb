namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

internal interface IAdminRepositoryAccess
{
    IAdminRepositoryClient GetRepositoryClient(string databaseName);

    /// <summary>
    /// Evicts the cached client for <paramref name="databaseName"/> so the next call builds a fresh client
    /// that authenticates anew — required after the database's user has been dropped and re-created,
    /// because the driver never re-authenticates an already-open connection (AB#4690). The evicted client
    /// is deliberately NOT disposed: live TenantContext / data-source instances may still hold it, and
    /// disposing would fail their in-flight operations with ObjectDisposedException('CoreServerSessionPool').
    /// No-op when nothing is cached.
    /// </summary>
    void Invalidate(string databaseName);
}