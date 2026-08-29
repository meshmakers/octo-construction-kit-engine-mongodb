using Meshmakers.Octo.Runtime.Contracts;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

public interface IRepositoryClient : IDisposable
{
    Task<IOctoSession> GetSessionAsync();

    /// <summary>
    ///     Gets a session acting for the given caller (carries the security context for RtCreatedBy
    ///     stamping and data-level permissions).
    /// </summary>
    Task<IOctoSession> GetSessionAsync(RtSecurityContext securityContext);

    IOctoSession GetSession();

    /// <summary>
    ///     Gets a session acting for the given caller (carries the security context for RtCreatedBy
    ///     stamping and data-level permissions).
    /// </summary>
    IOctoSession GetSession(RtSecurityContext securityContext);

    IRepository GetRepository(string name);
}