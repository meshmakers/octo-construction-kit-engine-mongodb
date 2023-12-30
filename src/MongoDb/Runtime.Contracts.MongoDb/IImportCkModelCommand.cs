namespace Meshmakers.Octo.Runtime.Contracts.MongoDb;

public interface IImportCkModelCommand
{
    Task ImportTextAsync(string tenantId, string jsonText,
        CancellationToken? cancellationToken = null);

    Task ImportAsync(string tenantId, string filePath,
        CancellationToken? cancellationToken = null);
}