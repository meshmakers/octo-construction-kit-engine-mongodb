using System.Collections.Generic;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

namespace Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;

public class TextSearchLanguageCacheItem
{
    public TextSearchLanguageCacheItem(string language)
    {
        Language = language;
        Fields = new List<CkIndexFields>();
    }

    public string Language { get; }

    public IList<CkIndexFields> Fields { get; }
}
