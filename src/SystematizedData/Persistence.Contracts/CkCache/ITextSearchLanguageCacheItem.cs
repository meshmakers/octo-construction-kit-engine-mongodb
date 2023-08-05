using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

namespace Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;

public interface ITextSearchLanguageCacheItem
{
    string Language { get; }
    IList<CkIndexFields> Fields { get; }
}