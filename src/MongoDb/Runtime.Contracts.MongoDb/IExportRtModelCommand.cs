using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb;

public interface IExportRtModelCommand
{
    Task ExportAsync(string tenantId, OctoObjectId queryId, string filePath,
        CancellationToken? cancellationToken);
}