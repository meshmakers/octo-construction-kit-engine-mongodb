using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Meshmakers.Octo.Backend.Persistence.DataAccess.Internal;
using Meshmakers.Octo.Backend.Persistence.DatabaseEntities;
using NLog;

namespace Meshmakers.Octo.Backend.Persistence.CkRuleEngine.Cache;

public class CkCache : ICkCache
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private readonly ConcurrentDictionary<string, EntityCacheItem> _metaCache;
    private bool _isInitialized;

    public CkCache(string dataSource)
    {
        TenantId = dataSource;
        _metaCache = new ConcurrentDictionary<string, EntityCacheItem>();
    }

    public string TenantId { get; }

    public async Task Initialize(IDatabaseContext databaseContext)
    {
        if (_isInitialized)
        {
            return;
        }

        Logger.Debug("Initializing MetaCache");

        var session = await databaseContext.StartSessionAsync();
        session.StartTransaction();

        var ckTypeInfosDictionary =
            (await databaseContext.GetCkTypeInfoAsync(session)).ToDictionary(k => k.CkId, v => v);

        foreach (var ckTypeInfo in ckTypeInfosDictionary.Values)
        {
            var entityCacheItem = new EntityCacheItem(ckTypeInfo);

            foreach (var attribute in ckTypeInfo.Attributes)
            {
                var ckAttribute =
                    await databaseContext.CkAttributes.FindSingleOrDefaultAsync(session, a =>
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
            BuildAttributes(entityCacheItem);
        }

        foreach (var entityCacheItem in _metaCache.Values)
        {
            var ckTypeInfo = ckTypeInfosDictionary[entityCacheItem.CkId];
            BuildAssociationGraph(ckTypeInfo.Associations.Out, entityCacheItem.OutboundAssociations,
                entityCacheItem.CkId,
                GraphDirections.Outbound);
            BuildAssociationGraph(ckTypeInfo.Associations.In, entityCacheItem.InboundAssociations, entityCacheItem.CkId,
                GraphDirections.Inbound);
        }

        await session.CommitTransactionAsync();

        _isInitialized = true;
        Logger.Debug("Initializing MetaCache done");
    }


    public void Dispose()
    {
        _metaCache.Clear();
        IsDisposed = true;
    }

    public bool IsDisposed { get; private set; }


    public IEnumerable<EntityCacheItem> GetCkEntities()
    {
        return _metaCache.Values;
    }


    public EntityCacheItem GetEntityCacheItem(string ckId)
    {
        return _metaCache[ckId];
    }


    private void BuildAssociationGraph(CkTypeAggregations ckTypeAggregations,
        IDictionary<string, List<AssociationCacheItem>> associations,
        string ckId, GraphDirections graphDirections)
    {
        Logger.Debug($"BuildAssociationGraph for '{ckId}' ({graphDirections})");

        var groupFunc = new Func<CkEntityAssociation, string>(association => association.InboundName);
        if (graphDirections == GraphDirections.Outbound)
        {
            groupFunc = association => association.OutboundName;
        }

        var ckEntityAssociationList = new List<CkEntityAssociation>();
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
            var roleAssociationItems = new List<AssociationCacheItem>();

            foreach (var entityAssociationByRole in entityAssociations.GroupBy(x => x.RoleId))
            {
                var baseTypesChain = new List<EntityCacheItem>();
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
        foreach (var attribute in cacheItem.Attributes)
        {
            if (!entityCacheItem.Attributes.ContainsKey(attribute.Key))
            {
                entityCacheItem.Attributes.Add(attribute.Key, attribute.Value);
            }
        }
    }

    private void BuildInheritanceGraph(CkTypeInfo ckTypeInfo, EntityCacheItem entityCacheItem)
    {
        Logger.Debug($"Building inheritance graph '{entityCacheItem.CkId}'");

        foreach (var ckBaseTypeInfo in ckTypeInfo.BaseTypes)
        {
            if (ckBaseTypeInfo.OriginCkId == entityCacheItem.CkId)
            {
                continue;
            }

            Logger.Debug($"Building inheritance graph '{entityCacheItem.CkId}', base type {ckBaseTypeInfo.OriginCkId}");
            var baseType = _metaCache[ckBaseTypeInfo.OriginCkId];
            if (ckBaseTypeInfo.TargetCkId == entityCacheItem.CkId)
            {
                entityCacheItem.BaseType = baseType;
            }

            baseType.DerivedTypes.Add(entityCacheItem);
        }
    }
}
