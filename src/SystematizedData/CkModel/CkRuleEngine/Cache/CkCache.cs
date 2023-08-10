using System.Collections.Concurrent;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using NLog;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.CkModel.CkRuleEngine.Cache;

public class CkCache : ICkCache
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly ConcurrentDictionary<CkId<CkTypeId>, EntityCacheItem> _metaCache;
    private bool _isInitialized;

    public CkCache(string tenantId)
    {
        TenantId = tenantId;
        _metaCache = new ConcurrentDictionary<CkId<CkTypeId>, EntityCacheItem>();
    }

    public string TenantId { get; }

    public async Task Initialize(ITenantCkModelRepository tenantCkModelRepository)
    {
        if (_isInitialized)
        {
            return;
        }

        Logger.Debug("Initializing CK cache");

        var session = await tenantCkModelRepository.StartSessionAsync();
        session.StartTransaction();

        var ckTypeInfosDictionary =
            (await tenantCkModelRepository.GetCkTypeInfoAsync(session)).ToDictionary(k => k.CkTypeId, v => v);

        var associationRoles = new Dictionary<CkId<CkAssociationRoleId>, CkAssociationRole>();
        Func<CkId<CkAssociationRoleId>, Task<CkAssociationRole>> getAssociationRoleFunc = async associationId =>
        {
            if (!associationRoles.TryGetValue(associationId, out var associationRole))
            {
                associationRole = await tenantCkModelRepository.GetCkAssociationRoleAsync(session, associationId);
                associationRoles[associationId] =
                    associationRole ?? throw ModelValidationException.CkAssociationRoleNotFound(associationId);
            }

            return associationRole;
        };


        foreach (var ckTypeInfo in ckTypeInfosDictionary.Values)
        {
            var entityCacheItem = new EntityCacheItem(ckTypeInfo);

            foreach (var attribute in ckTypeInfo.Attributes)
            {
                var ckAttribute =
                    await tenantCkModelRepository.FindSingleOrDefaultCkAttributesAsync(session, a =>
                        a.AttributeId == attribute.AttributeId);
                entityCacheItem.Attributes.Add(attribute.AttributeName,
                    new AttributeCacheItem(attribute.AttributeName, attribute, ckAttribute));
            }

            if (ckTypeInfo.TextSearchLanguages != null)
            {
                foreach (var textSearchLanguage in ckTypeInfo.TextSearchLanguages)
                {
                    var textSearchCacheItem = new TextSearchLanguageCacheItem(textSearchLanguage.Language ?? "en");

                    foreach (var textSearchField in textSearchLanguage.Fields)
                    {
                        textSearchCacheItem.Fields.Add(textSearchField);
                    }

                    entityCacheItem.TextSearchLanguages.Add(textSearchCacheItem);
                }
            }

            _metaCache[ckTypeInfo.CkTypeId] = entityCacheItem;
        }

        foreach (var entityCacheItem in _metaCache.Values)
        {
            BuildInheritanceGraph(ckTypeInfosDictionary[entityCacheItem.CkTypeId], entityCacheItem);
        }

        foreach (var entityCacheItem in _metaCache.Values)
        {
            BuildAttributes(entityCacheItem);

            var ckTypeInfo = ckTypeInfosDictionary[entityCacheItem.CkTypeId];
            await BuildAssociationGraphAsync(ckTypeInfo.Associations.Out, getAssociationRoleFunc, entityCacheItem.OutboundAssociations,
                entityCacheItem.CkTypeId,
                GraphDirections.Outbound);
            await BuildAssociationGraphAsync(ckTypeInfo.Associations.In, getAssociationRoleFunc, entityCacheItem.InboundAssociations,
                entityCacheItem.CkTypeId,
                GraphDirections.Inbound);
        }

        await session.CommitTransactionAsync();

        _isInitialized = true;
        Logger.Debug("Initializing CK cache done");
    }

    public void Unload()
    {
        Logger.Debug("Unloading CK cache");

        if (!_isInitialized)
        {
            Logger.Debug("CK cache is not initialized");
            return;
        }

        _metaCache.Clear();

        _isInitialized = false;
        Logger.Debug("Unloading CK cache done");
    }


    public void Dispose()
    {
        _metaCache.Clear();
        IsDisposed = true;
    }

    public bool IsDisposed { get; private set; }


    public IEnumerable<IEntityCacheItem> GetCkEntities()
    {
        return _metaCache.Values;
    }


    public IEntityCacheItem GetEntityCacheItem(CkId<CkTypeId> ckTypeId)
    {
        return _metaCache[ckTypeId];
    }


    private async Task BuildAssociationGraphAsync(CkTypeAggregations ckTypeAggregations,
        Func<CkId<CkAssociationRoleId>, Task<CkAssociationRole>> getAssociationRoleFunc,
        IDictionary<string, List<IAssociationCacheItem>> associations,
        CkId<CkTypeId> ckTypeId, GraphDirections graphDirections)
    {
        Logger.Debug($"BuildAssociationGraph for '{ckTypeId}' ({graphDirections})");

        var ckEntityAssociationList = new List<CkEntityAssociation>();
        if (ckTypeAggregations.Owned != null)
        {
            ckEntityAssociationList.AddRange(ckTypeAggregations.Owned);
        }

        if (ckTypeAggregations.Inherited != null)
        {
            ckEntityAssociationList.AddRange(ckTypeAggregations.Inherited);
        }

        var ckEntityAssociationCompleteList = await Task.WhenAll(ckEntityAssociationList.Select(
            async x => new {OriginCkTypeId = x.OriginCkTypeId, TargetCkTypeId = x.TargetCkTypeId, AssocationRole = await getAssociationRoleFunc(x.RoleId) }).ToList());

        var groupedAssocList = ckEntityAssociationCompleteList.GroupBy(x => x.AssocationRole.InboundName);
        if (graphDirections == GraphDirections.Outbound)
        {
            groupedAssocList = ckEntityAssociationCompleteList.GroupBy(x => x.AssocationRole.OutboundName);
        }

        foreach (var entityAssociations in groupedAssocList)
        {
            var roleAssociationItems = new List<IAssociationCacheItem>();

            foreach (var entityAssociationByRole in entityAssociations.GroupBy(x => x.AssocationRole.RoleId))
            {
                var baseTypesChain = new List<IEntityCacheItem>();
                foreach (var entityAssociation in entityAssociationByRole)
                {
                    var targetEntityId = graphDirections == GraphDirections.Inbound
                        ? entityAssociation.OriginCkTypeId.SemanticVersionedFullName
                        : entityAssociation.TargetCkTypeId.SemanticVersionedFullName;
                    var targetInfo = _metaCache[targetEntityId];
                    baseTypesChain.AddRange(targetInfo.GetAllDerivedTypes(true));
                }

                roleAssociationItems.Add(new AssociationCacheItem(
                        graphDirections == GraphDirections.Inbound
                            ? entityAssociationByRole.First().AssocationRole.InboundName
                            : entityAssociationByRole.First().AssocationRole.OutboundName,
                        entityAssociationByRole.Key,
                        entityAssociationByRole.First().AssocationRole.InboundMultiplicity,
                        entityAssociationByRole.First().AssocationRole.OutboundMultiplicity,
                        baseTypesChain.Where(x => !x.IsAbstract).ToList()
                    )
                );
            }

            associations.Add(entityAssociations.Key, roleAssociationItems);
        }
    }

    private void BuildAttributes(EntityCacheItem entityCacheItem)
    {
        Logger.Debug($"Building attributes for '{entityCacheItem.CkTypeId}'");

        foreach (var cacheItem in entityCacheItem.GetBaseTypesChain(false))
        {
            foreach (var attribute in cacheItem.Attributes)
            {
                if (!entityCacheItem.Attributes.ContainsKey(attribute.Key))
                {
                    entityCacheItem.Attributes.Add(attribute.Key, attribute.Value);
                }
            }
        }
    }

    private void BuildInheritanceGraph(CkTypeInfo ckTypeInfo, EntityCacheItem entityCacheItem)
    {
        Logger.Debug($"Building inheritance graph '{entityCacheItem.CkTypeId}'");

        foreach (var ckBaseTypeInfo in ckTypeInfo.BaseTypes)
        {
            if (ckBaseTypeInfo.OriginCkTypeId.Equals(entityCacheItem.CkTypeId))
            {
                continue;
            }

            Logger.Debug($"Building inheritance graph '{entityCacheItem.CkTypeId}', base type {ckBaseTypeInfo.OriginCkTypeId}");
            var baseType = _metaCache[ckBaseTypeInfo.OriginCkTypeId];
            if (ckBaseTypeInfo.TargetCkTypeId.Equals(entityCacheItem.CkTypeId))
            {
                entityCacheItem.BaseType = baseType;
            }

            baseType.DerivedTypes.Add(entityCacheItem);
        }
    }
}