namespace Meshmakers.Octo.SystematizedData.Persistence.Commands;

public interface IImportCkModelCommand
{
    Task ImportTextAsync(string tenantId, string jsonText,
        CancellationToken? cancellationToken = null);

    Task ImportAsync(string tenantId, string filePath,
        CancellationToken? cancellationToken = null);
}