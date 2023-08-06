namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

public class DataQueryOperation
{
    public DataQueryOperation()
    {
        Language = "en";
    }

    public string Language { get; set; }

    public TextSearchFilter? TextSearchFilter { get; set; }

    public AttributeSearchFilter? AttributeSearchFilter { get; set; }

    public ICollection<FieldFilter>? FieldFilters { get; set; }

    public ICollection<SortOrderItem>? SortOrders { get; set; }

    public void AppendFieldFilter(string attributeName, FieldFilterOperator comparisonOperator, object? comparisonValue)
    {
        FieldFilters ??= new List<FieldFilter>();

        FieldFilters.Add(new FieldFilter(attributeName, comparisonOperator, comparisonValue));
    }
}