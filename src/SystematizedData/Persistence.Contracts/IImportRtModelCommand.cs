namespace Meshmakers.Octo.SystematizedData.Persistence.Commands;

public interface IImportRtModelCommand
{
    Task ImportText(string tenantId, string jsonText, CancellationToken? cancellationToken = null);
    Task Import(string tenantId, string filePath, CancellationToken? cancellationToken = null);
}