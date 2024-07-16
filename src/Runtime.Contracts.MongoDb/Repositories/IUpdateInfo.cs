namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;

public interface IUpdateInfo<out T>
{
    string[] UpdateFields { get; }
    UpdateTypes UpdateType { get; }
    T? Document { get; }
    T? DocumentBeforeChange { get; }
}