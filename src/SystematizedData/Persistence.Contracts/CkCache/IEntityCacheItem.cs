using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;

public interface IEntityCacheItem
{
    CkId<CkTypeId> CkTypeId { get; }
    bool IsAbstract { get; }
    bool IsFinal { get; }
    IEntityCacheItem? BaseType { get; }
    IList<IEntityCacheItem> DerivedTypes { get; }
    IList<ITextSearchLanguageCacheItem> TextSearchLanguages { get; }
    IDictionary<string, IAttributeCacheItem> Attributes { get; }
    IDictionary<string, List<IAssociationCacheItem>> OutboundAssociations { get; }
    IDictionary<string, List<IAssociationCacheItem>> InboundAssociations { get; }
    IEnumerable<IEntityCacheItem> GetAllDerivedTypes(bool includeSelf);
    IEnumerable<IEntityCacheItem> GetBaseTypesChain(bool includeSelf);
}