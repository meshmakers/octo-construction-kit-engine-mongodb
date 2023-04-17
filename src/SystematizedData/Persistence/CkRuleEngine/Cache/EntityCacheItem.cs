using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

namespace Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;

[DebuggerDisplay("{" + nameof(CkId) + "}")]
public class EntityCacheItem
{
    public EntityCacheItem(CkTypeInfo ckTypeInfo)
    {
        CkId = ckTypeInfo.CkId;
        IsFinal = ckTypeInfo.IsFinal;
        IsAbstract = ckTypeInfo.IsAbstract;
        ScopeId = ckTypeInfo.ScopeId;
        DerivedTypes = new List<EntityCacheItem>();
        Attributes = new Dictionary<string, AttributeCacheItem>();
        TextSearchLanguages = new List<TextSearchLanguageCacheItem>();
        OutboundAssociations = new Dictionary<string, List<AssociationCacheItem>>();
        InboundAssociations = new Dictionary<string, List<AssociationCacheItem>>();
    }


    public string CkId { get; }

    public bool IsAbstract { get; }
    public bool IsFinal { get; }
    public ScopeIds ScopeId { get; }

    public EntityCacheItem BaseType { get; internal set; }
    public IList<EntityCacheItem> DerivedTypes { get; }
    public IList<TextSearchLanguageCacheItem> TextSearchLanguages { get; }

    public IDictionary<string, AttributeCacheItem> Attributes { get; }
    public IDictionary<string, List<AssociationCacheItem>> OutboundAssociations { get; }
    public IDictionary<string, List<AssociationCacheItem>> InboundAssociations { get; }

    public IEnumerable<EntityCacheItem> GetAllDerivedTypes(bool includeSelf)
    {
        var list = new List<EntityCacheItem>();

        var currentItem = new List<EntityCacheItem>(new[] { this });
        if (!includeSelf)
        {
            currentItem.Clear();
            currentItem.AddRange(DerivedTypes);
        }

        while (currentItem.Any())
        {
            list.AddRange(currentItem);
            var tmp = currentItem.SelectMany(x => x.DerivedTypes).ToArray();
            currentItem.Clear();
            currentItem.AddRange(tmp);
        }

        return list;
    }

    public IEnumerable<EntityCacheItem> GetBaseTypesChain(bool includeSelf)
    {
        var list = new List<EntityCacheItem>();

        var currentItem = this;
        if (!includeSelf)
        {
            currentItem = BaseType;
        }

        while (currentItem != null)
        {
            list.Add(currentItem);
            currentItem = currentItem.BaseType;
        }

        return list;
    }
}
