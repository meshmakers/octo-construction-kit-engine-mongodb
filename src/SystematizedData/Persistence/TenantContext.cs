using System.Threading;
using System.Threading.Tasks;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Backend.Persistence.CkRuleEngine.Cache;
using Meshmakers.Octo.Backend.Persistence.Commands;
using Meshmakers.Octo.Backend.Persistence.DataAccess;
using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.Backend.Persistence;

internal class TenantContext : ITenantContextInternal
{
    public TenantContext(string dataSource, ITenantRepositoryInternal tenantRepository, ICkCache ckCache)
    {
        TenantId = dataSource;
        InternalRepository = tenantRepository;
        CkCache = ckCache;
    }

    public string TenantId { get; }

    public ITenantRepository Repository => InternalRepository;

    public ITenantRepositoryInternal InternalRepository { get; }

    public ICkCache CkCache { get; }

    public async Task ExportRtModelAsync(IOctoSession session, OctoObjectId queryId, string filePath,
        CancellationToken? cancellationToken)
    {
        ArgumentValidation.ValidateExistingFile(nameof(filePath), filePath);

        var exporter = new ExportRtModel(this);
        await exporter.Export(session, queryId, filePath, cancellationToken);
    }

    public async Task ImportRtModelAsync(IOctoSession session, string filePath, CancellationToken? cancellationToken)
    {
        ArgumentValidation.ValidateFilePath(nameof(filePath), filePath);

        var importer = new ImportRtModel(this);
        await importer.Import(session, filePath, cancellationToken);
    }

    public async Task ImportRtModelAsTextAsync(IOctoSession session, string jsonText)
    {
        ArgumentValidation.ValidateString(nameof(jsonText), jsonText);

        var importer = new ImportRtModel(this);
        await importer.ImportText(session, jsonText);
    }
}
