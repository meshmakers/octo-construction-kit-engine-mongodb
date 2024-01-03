namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository;

public interface IUpdateInfo<out T>
{
    string[] UpdateFields { get; }
    UpdateTypes UpdateType { get; }
    T? Document { get; }
    T? DocumentBeforeChange { get; }
}