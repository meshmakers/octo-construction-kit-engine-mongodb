namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

public interface IUpdateInfo<out T>
{
    string[] UpdateFields { get; }
    UpdateTypes UpdateType { get; }
    T? Document { get; }
    T? DocumentBeforeChange { get; }
}