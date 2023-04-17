namespace Meshmakers.Octo.SystematizedData.Persistence.MongoDb;

internal interface IRepositoryInternal : IRepository
{
    string GetCollectionName<T>(string? suffix = null) where T : class, new();
}
