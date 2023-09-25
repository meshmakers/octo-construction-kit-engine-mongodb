using System.Collections.Concurrent;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Serialization;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using Microsoft.Extensions.Logging;
using Persistence.InternalContracts;
using RtAssociation = Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities.RtAssociation;
using RtEntity = Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities.RtEntity;

namespace Meshmakers.Octo.SystematizedData.Persistence.Commands;

public class ImportRtModelCommand : IImportRtModelCommand
{
    private readonly ILogger<ImportRtModelCommand> _logger;
    private readonly ISystemContextInternal _systemContext;
    private const int Max = 5000;
    private readonly HashSet<OctoObjectId> _entityImportIds;
    private readonly ConcurrentQueue<RtAssociation> _importAssociationQueue;

    private readonly ConcurrentQueue<RtEntity> _importEntityQueue;
    private int _associationsCount;

    private int _entityProgressCount;

    public ImportRtModelCommand(ILogger<ImportRtModelCommand> logger, ISystemContextInternal systemContext)
    {
        _logger = logger;
        _systemContext = systemContext;

        _entityImportIds = new HashSet<OctoObjectId>();
        _importEntityQueue = new ConcurrentQueue<RtEntity>();
        _importAssociationQueue = new ConcurrentQueue<RtAssociation>();
    }

    public async Task ImportText(string tenantId, string jsonText, CancellationToken? cancellationToken = null)
    {
        _logger.LogInformation("Importing RT entities using text started");

        var tenantContext = await _systemContext.GetChildTenantContextInternalAsync(tenantId);
        var tenantRepository = await tenantContext.GetTenantRepositoryInternalAsync();

        var session = await tenantRepository.GetSessionAsync();
        try
        {
            session.StartTransaction();

            using (var stream = new StreamReader(jsonText))
            {
                await RtSerializer.DeserializeAsync(stream, x => ImportEntity(session, x, tenantRepository), cancellationToken);
            }

            // Finish the last entities
            await ImportToDatabase(session, tenantRepository);

            _logger.LogInformation("{Count} entities, {AssociationsCount} associations imported", _entityImportIds.Count, _associationsCount);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Import of RT model failed");
            throw;
        }
    }

    public async Task Import(string tenantId, string filePath, CancellationToken? cancellationToken = null)
    {
        _logger.LogInformation("Importing RT entities using file started");

        var session = await _systemContext.GetSystemSessionAsync();
        try
        {
            session.StartTransaction();
            var tenantContext = await _systemContext.GetChildTenantContextInternalAsync(tenantId);
            var tenantRepository = await tenantContext.GetTenantRepositoryInternalAsync();

            using (var stream = File.OpenText(filePath))
            {
                await RtSerializer.DeserializeAsync(stream, x => ImportEntity(session, x, tenantRepository), cancellationToken);
            }

            // Finish the last entities
            await ImportToDatabase(session, tenantRepository);

            _logger.LogInformation("{Count} entities, {AssociationsCount} associations imported", _entityImportIds.Count, _associationsCount);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Import of RT model failed");
            throw;
        }
    }

    private async Task ImportEntity(IOctoSession session, RtEntityDto modelRtEntity, ITenantRepositoryInternal tenantRepository)
    {
        var progress = Interlocked.Increment(ref _entityProgressCount);
        if (progress > Max)
        {
            _logger.LogInformation("{EntityCount} entities (total imports of {Count}) imported", _importEntityQueue.Count, _entityImportIds.Count);
            Interlocked.Exchange(ref _entityProgressCount, 1);

            await ImportToDatabase(session, tenantRepository);
        }

        var entityCacheItem = tenantRepository.GetEntityCacheItem(modelRtEntity.CkTypeId);

        var rtEntity = tenantRepository.CreateTransientRtEntity(modelRtEntity.CkTypeId);
        rtEntity.RtId = modelRtEntity.RtId;
        rtEntity.RtChangedDateTime = modelRtEntity.RtChangedDateTime;
        rtEntity.RtCreationDateTime = modelRtEntity.RtCreationDateTime;
        rtEntity.RtWellKnownName = modelRtEntity.RtWellKnownName;

        if (_entityImportIds.Contains(rtEntity.RtId))
        {
            _logger.LogError("'{RtEntityRtId}' already imported", rtEntity.RtId);
            return;
        }

        _entityImportIds.Add(rtEntity.RtId);

        foreach (var modelAttribute in modelRtEntity.Attributes)
        {
            var attributeCacheItem =
                entityCacheItem.AllAttributes.Values.FirstOrDefault(a => a.CkAttributeId.Equals(modelAttribute.Id));
            if (attributeCacheItem == null)
            {
                _logger.LogError("'{ModelAttributeId}' does not exit on type '{CkTypeId}'", modelAttribute.Id, entityCacheItem.CkTypeId);
                return;
            }

            rtEntity.SetAttributeValue(attributeCacheItem.AttributeName, attributeCacheItem.ValueType,
                modelAttribute.Value);
        }

        _importEntityQueue.Enqueue(rtEntity);

        if (modelRtEntity.Associations != null && modelRtEntity.Associations.Count > 0)
        {
            var originId = rtEntity.RtId;

            foreach (var association in modelRtEntity.Associations)
            {
                var rtAssociation = new RtAssociation
                {
                    AssociationRoleId = association.RoleId,
                    OriginRtId = originId,
                    OriginCkTypeId = rtEntity.CkTypeId,
                    TargetRtId = association.TargetRtId,
                    TargetCkTypeId = association.TargetCkTypeId
                };
                _importAssociationQueue.Enqueue(rtAssociation);
                Interlocked.Increment(ref _associationsCount);
            }
        }
    }

    private async Task ImportToDatabase(IOctoSession session, ITenantRepositoryInternal tenantRepository)
    {
        _logger.LogInformation("Importing {Count} to database", _importEntityQueue.Count);

        try
        {
            var importEntities = new List<RtEntity>();
            var importAssociations = new List<RtAssociation>();

            var entityMax = _importEntityQueue.Count;
            var associationsMax = _importAssociationQueue.Count;

            for (var i = 0; i < entityMax; i++)
            {
                if (_importEntityQueue.TryDequeue(out var tmp))
                {
                    importEntities.Add(tmp);
                }
                else
                {
                    break;
                }
            }

            for (var i = 0; i < associationsMax; i++)
            {
                if (_importAssociationQueue.TryDequeue(out var tmp))
                {
                    importAssociations.Add(tmp);
                }
                else
                {
                    break;
                }
            }

            if (importEntities.Any())
            {
                _logger.LogInformation("Adding entities...");
                await tenantRepository.BulkInsertRtEntitiesAsync(session, importEntities);
            }

            if (importAssociations.Any())
            {
                _logger.LogInformation("Adding associations...");
                await tenantRepository.BulkRtAssociationsAsync(session, importAssociations);
            }


            _logger.LogInformation("Add to database completed");
        }
        catch (Exception e)
        {
            CommandExecutionFailedException.BulkImportError(e);
        }
    }
}