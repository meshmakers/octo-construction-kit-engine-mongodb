using System.Collections.Concurrent;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using NLog;

namespace Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;

public class CkCache : ICkCache
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly ConcurrentDictionary<CkTypeId, EntityCacheItem> _metaCache;
    private bool _isInitialized;

    public CkCache(string tenantId)
    {
        TenantId = tenantId;
        _metaCache = new ConcurrentDictionary<CkTypeId, EntityCacheItem>();
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
            (await tenantCkModelRepository.GetCkTypeInfoAsync(session)).ToDictionary(k => k.CkId, v => v);

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
                    var textSearchCacheItem = new TextSearchLanguageCacheItem(textSearchLanguage.Language);

                    foreach (var textSearchField in textSearchLanguage.Fields)
                    {
                        textSearchCacheItem.Fields.Add(textSearchField);
                    }

                    entityCacheItem.TextSearchLanguages.Add(textSearchCacheItem);
                }
            }

            _metaCache[ckTypeInfo.CkId] = entityCacheItem;
        }

        foreach (var entityCacheItem in _metaCache.Values)
        {
            BuildInheritanceGraph(ckTypeInfosDictionary[entityCacheItem.CkId], entityCacheItem);
        }

        foreach (var entityCacheItem in _metaCache.Values)
        {
            BuildAttributes(entityCacheItem);

            var ckTypeInfo = ckTypeInfosDictionary[entityCacheItem.CkId];
            BuildAssociationGraph(ckTypeInfo.Associations.Out, entityCacheItem.OutboundAssociations,
                entityCacheItem.CkId,
                GraphDirections.Outbound);
            BuildAssociationGraph(ckTypeInfo.Associations.In, entityCacheItem.InboundAssociations, entityCacheItem.CkId,
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


    public IEntityCacheItem GetEntityCacheItem(CkTypeId ckId)
    {
        return _metaCache[ckId];
    }


    private void BuildAssociationGraph(ICkTypeAggregations ckTypeAggregations,
        IDictionary<string, List<IAssociationCacheItem>> associations,
        CkTypeId ckId, GraphDirections graphDirections)
    {
        Logger.Debug($"BuildAssociationGraph for '{ckId}' ({graphDirections})");

        var groupFunc = new Func<ICkEntityAssociation, string>(association => association.InboundName);
        if (graphDirections == GraphDirections.Outbound)
        {
            groupFunc = association => association.OutboundName;
        }

        var ckEntityAssociationList = new List<ICkEntityAssociation>();
        if (ckTypeAggregations.Owned != null)
        {
            ckEntityAssociationList.AddRange(ckTypeAggregations.Owned);
        }

        if (ckTypeAggregations.Inherited != null)
        {
            ckEntityAssociationList.AddRange(ckTypeAggregations.Inherited);
        }

        foreach (var entityAssociations in ckEntityAssociationList.GroupBy(groupFunc))
        {
            var roleAssociationItems = new List<IAssociationCacheItem>();

            foreach (var entityAssociationByRole in entityAssociations.GroupBy(x => x.RoleId))
            {
                var baseTypesChain = new List<IEntityCacheItem>();
                foreach (var entityAssociation in entityAssociationByRole)
                {
                    var targetEntityId = graphDirections == GraphDirections.Inbound
                        ? entityAssociation.OriginCkId
                        : entityAssociation.TargetCkId;
                    var targetInfo = _metaCache[targetEntityId];
                    baseTypesChain.AddRange(targetInfo.GetAllDerivedTypes(true));
                }

                roleAssociationItems.Add(new AssociationCacheItem
                {
                    RoleId = entityAssociationByRole.Key,
                    Name = graphDirections == GraphDirections.Inbound
                        ? entityAssociationByRole.First().InboundName
                        : entityAssociationByRole.First().OutboundName,
                    InboundMultiplicity = entityAssociationByRole.First().InboundMultiplicity,
                    OutboundMultiplicity = entityAssociationByRole.First().OutboundMultiplicity,
                    AllowedTypes = baseTypesChain.Where(x => !x.IsAbstract).ToList()
                });
            }

            associations.Add(entityAssociations.Key, roleAssociationItems);
        }
    }

    private void BuildAttributes(EntityCacheItem entityCacheItem)
    {
        Logger.Debug($"Building attributes for '{entityCacheItem.CkId}'");
        
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

    private void BuildInheritanceGraph(ICkTypeInfo ckTypeInfo, EntityCacheItem entityCacheItem)
    {
        Logger.Debug($"Building inheritance graph '{entityCacheItem.CkId}'");

        foreach (var ckBaseTypeInfo in ckTypeInfo.BaseTypes)
        {
            if (ckBaseTypeInfo.OriginCkId.Equals(entityCacheItem.CkId))
            {
                continue;
            }

            Logger.Debug($"Building inheritance graph '{entityCacheItem.CkId}', base type {ckBaseTypeInfo.OriginCkId}");
            var baseType = _metaCache[ckBaseTypeInfo.OriginCkId];
            if (ckBaseTypeInfo.TargetCkId.Equals(entityCacheItem.CkId))
            {
                entityCacheItem.BaseType = baseType;
            }

            baseType.DerivedTypes.Add(entityCacheItem);
        }
    }
}
