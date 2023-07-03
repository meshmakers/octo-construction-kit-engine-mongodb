using System.Collections.Generic;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

public class AttributeSearchFilter
{
    public AttributeSearchFilter(IEnumerable<string> attributeNames, object searchTerm)
    {
        AttributeNames = attributeNames;
        SearchTerm = searchTerm;
    }

    public object SearchTerm { get; }

    public IEnumerable<string> AttributeNames { get; }
}
