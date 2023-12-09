namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

internal interface IRepositoryInternal : IRepository
{
    string GetCollectionName<T>(string? suffix = null) where T : class, new();
}