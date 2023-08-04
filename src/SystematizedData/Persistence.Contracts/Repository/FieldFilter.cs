using Meshmakers.Common.Shared;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

public class FieldFilter
{
    public FieldFilter(string attributeName, FieldFilterOperator comparisonOperator, object? comparisonValue)
    {
        ArgumentValidation.ValidateString(nameof(attributeName), attributeName);

        AttributeName = attributeName;
        Operator = comparisonOperator;
        ComparisonValue = comparisonValue;
    }

    public string AttributeName { get; }
    public FieldFilterOperator Operator { get; }
    public object? ComparisonValue { get; }
}
