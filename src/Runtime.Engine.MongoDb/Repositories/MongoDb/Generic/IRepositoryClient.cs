using Meshmakers.Octo.Runtime.Contracts;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

public interface IRepositoryClient : IDisposable
{
    Task<IOctoSession> GetSessionAsync();

    IOctoSession GetSession();

    IRepository GetRepository(string name);
}