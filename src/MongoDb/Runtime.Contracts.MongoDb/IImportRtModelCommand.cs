namespace Meshmakers.Octo.Runtime.Contracts.MongoDb;

public interface IImportRtModelCommand
{
    Task ImportText(string tenantId, string jsonText, CancellationToken? cancellationToken = null);
    Task Import(string tenantId, string filePath, CancellationToken? cancellationToken = null);
}