using System.Collections.Generic;

namespace Meshmakers.Octo.Backend.Persistence.DataAccess;

public class DataQueryOperation
{
    public DataQueryOperation()
    {
        Language = "en";
    }

    public string Language { get; set; }

    public TextSearchFilter TextSearchFilter { get; set; }

    public AttributeSearchFilter AttributeSearchFilter { get; set; }

    public IEnumerable<FieldFilter> FieldFilters { get; set; }

    public IEnumerable<SortOrderItem> SortOrders { get; set; }
}
