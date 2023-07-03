using System.Collections.Generic;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

namespace Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;

public class TextSearchLanguageCacheItem : ITextSearchLanguageCacheItem
{
    public TextSearchLanguageCacheItem(string language)
    {
        Language = language;
        Fields = new List<ICkIndexFields>();
    }

    public string Language { get; }

    public IList<ICkIndexFields> Fields { get; }
}
