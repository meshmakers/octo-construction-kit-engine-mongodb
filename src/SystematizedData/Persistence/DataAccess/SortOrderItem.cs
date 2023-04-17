using Meshmakers.Common.Shared;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

public class SortOrderItem
{
    public SortOrderItem(string attributeName, SortOrders sortOrder)
    {
        ArgumentValidation.ValidateString(nameof(attributeName), attributeName);

        AttributeName = attributeName;
        SortOrder = sortOrder;
    }

    public string AttributeName { get; }
    public SortOrders SortOrder { get; }
}
