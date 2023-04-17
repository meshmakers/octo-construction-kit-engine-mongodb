using System.Collections.Generic;
using Meshmakers.Octo.Backend.Persistence.DatabaseEntities;

namespace Meshmakers.Octo.Backend.Persistence.CkRuleEngine.Cache;

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
