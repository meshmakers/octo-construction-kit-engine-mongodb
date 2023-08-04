using System.Collections.Concurrent;
using System.Diagnostics;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Common.Shared.Exchange;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using NLog;
using Persistence.InternalContracts;
using RtAssociation = Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities.RtAssociation;
using RtEntity = Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities.RtEntity;

namespace Meshmakers.Octo.SystematizedData.Persistence.Commands;

public class ImportRtModelCommand : IImportRtModelCommand
{
    private readonly ISystemContextInternal _systemContext;
    private const int Max = 5000;
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly HashSet<OctoObjectId> _entityImportIds;
    private readonly ConcurrentQueue<RtAssociation> _importAssociationQueue;

    private readonly ConcurrentQueue<RtEntity> _importEntityQueue;
    private ITenantRepositoryInternal? _tenantRepository;
    private int _associationsCount;

    private int _entityProgressCount;

    public ImportRtModelCommand(ISystemContextInternal systemContext)
    {
        _systemContext = systemContext;

        _entityImportIds = new HashSet<OctoObjectId>();
        _importEntityQueue = new ConcurrentQueue<RtEntity>();
        _importAssociationQueue = new ConcurrentQueue<RtAssociation>();
    }

    public async Task ImportText(string tenantId, string jsonText, CancellationToken? cancellationToken = null)
    {
        Logger.Info("Importing RT entities using text started.");

        _tenantRepository = await _systemContext.CreateOrGetTenantRepositoryInternalAsync(tenantId);

        var session = await _tenantRepository.StartSessionAsync();
        try
        {
            session.StartTransaction();

            using (var stream = new StreamReader(jsonText))
            {
                await RtSerializer.DeserializeAsync(stream, x => ImportEntity(session, x), cancellationToken);
            }

            // Finish the last entities
            await ImportToDatabase(session);

            Logger.Info($"{_entityImportIds.Count} entities, {_associationsCount} associations imported.");
        }
        catch (Exception e)
        {
            Logger.Error(e, "Import of RT model failed.");
            throw;
        }
    }

    public async Task Import(string tenantId, string filePath, CancellationToken? cancellationToken = null)
    {
        Logger.Info("Importing RT entities using file started.");

        var session = await _systemContext.StartSystemSessionAsync();
        try
        {
            session.StartTransaction();
            _tenantRepository = await _systemContext.CreateOrGetTenantRepositoryInternalAsync(tenantId);

            using (var stream = File.OpenText(filePath))
            {
                await RtSerializer.DeserializeAsync(stream, x => ImportEntity(session, x), cancellationToken);
            }

            // Finish the last entities
            await ImportToDatabase(session);

            Logger.Info($"{_entityImportIds.Count} entities, {_associationsCount} associations imported.");
        }
        catch (Exception e)
        {
            Logger.Error(e, "Import of RT model failed.");
            throw;
        }
    }

    private async Task ImportEntity(IOctoSession session, Common.Shared.Exchange.RtEntity modelRtEntity)
    {
        var progress = Interlocked.Increment(ref _entityProgressCount);
        if (progress > Max)
        {
            Logger.Info($"{_importEntityQueue.Count} entities (total imports of {_entityImportIds.Count}) imported.");
            Interlocked.Exchange(ref _entityProgressCount, 1);

            await ImportToDatabase(session);
        }

        Debug.Assert(_tenantRepository != null, nameof(_tenantRepository) + " != null");
        var entityCacheItem = _tenantRepository.GetEntityCacheItem(modelRtEntity.CkId);

        var rtEntity = _tenantRepository.CreateTransientRtEntity(modelRtEntity.CkId);
        rtEntity.RtId = modelRtEntity.RtId;
        rtEntity.RtChangedDateTime = modelRtEntity.RtChangedDateTime;
        rtEntity.RtCreationDateTime = modelRtEntity.RtCreationDateTime;
        rtEntity.RtWellKnownName = modelRtEntity.RtWellKnownName;

        if (_entityImportIds.Contains(rtEntity.RtId))
        {
            Logger.Error($"{rtEntity.RtId} already imported.");
            return;
        }

        _entityImportIds.Add(rtEntity.RtId);

        foreach (var modelAttribute in modelRtEntity.Attributes)
        {
            var attributeCacheItem =
                entityCacheItem.Attributes.Values.FirstOrDefault(a => a.AttributeId.Equals(modelAttribute.Id));
            if (attributeCacheItem == null)
            {
                Logger.Error($"'{modelAttribute.Id}' does not exit on type $'{entityCacheItem.CkId}'");
                return;
            }

            rtEntity.SetAttributeValue(attributeCacheItem.AttributeName, attributeCacheItem.AttributeValueType,
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
                    OriginCkId = rtEntity.CkId,
                    TargetRtId = association.TargetRtId,
                    TargetCkId = association.TargetCkId
                };
                _importAssociationQueue.Enqueue(rtAssociation);
                Interlocked.Increment(ref _associationsCount);
            }
        }
    }

    private async Task ImportToDatabase(IOctoSession session)
    {
        Logger.Info($"Importing {_importEntityQueue.Count} to database.");

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
                Logger.Info("Adding entities...");
                await _tenantRepository.BulkInsertRtEntitiesAsync(session, importEntities);
            }

            if (importAssociations.Any())
            {
                Logger.Info("Adding associations...");
                await _tenantRepository.BulkRtAssociationsAsync(session, importAssociations);
            }


            Logger.Info("Add to database completed.");
        }
        catch (Exception e)
        {
            throw new ModelImportException("Import of model failed during import to database.", e);
        }
    }
}