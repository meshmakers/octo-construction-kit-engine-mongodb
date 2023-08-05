using System.Diagnostics;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.CkModel.CkRuleEngine.Cache;

[DebuggerDisplay("{" + nameof(CkId) + "}")]
public class EntityCacheItem : IEntityCacheItem
{
    public EntityCacheItem(CkTypeInfo ckTypeInfo)
    {
        CkId = ckTypeInfo.CkId;
        IsFinal = ckTypeInfo.IsFinal;
        IsAbstract = ckTypeInfo.IsAbstract;
        DerivedTypes = new List<IEntityCacheItem>();
        Attributes = new Dictionary<string, IAttributeCacheItem>();
        TextSearchLanguages = new List<ITextSearchLanguageCacheItem>();
        OutboundAssociations = new Dictionary<string, List<IAssociationCacheItem>>();
        InboundAssociations = new Dictionary<string, List<IAssociationCacheItem>>();
    }


    public CkId<CkTypeId> CkId { get; }

    public bool IsAbstract { get; }
    public bool IsFinal { get; }

    public IEntityCacheItem? BaseType { get; internal set; }
    public IList<IEntityCacheItem> DerivedTypes { get; }
    public IList<ITextSearchLanguageCacheItem> TextSearchLanguages { get; }

    public IDictionary<string, IAttributeCacheItem> Attributes { get; }
    public IDictionary<string, List<IAssociationCacheItem>> OutboundAssociations { get; }
    public IDictionary<string, List<IAssociationCacheItem>> InboundAssociations { get; }

    public IEnumerable<IEntityCacheItem> GetAllDerivedTypes(bool includeSelf)
    {
        var list = new List<IEntityCacheItem>();

        var currentItem = new List<IEntityCacheItem>(new[] { this });
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

    public IEnumerable<IEntityCacheItem> GetBaseTypesChain(bool includeSelf)
    {
        var list = new List<IEntityCacheItem>();

        IEntityCacheItem? currentItem = this;
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
