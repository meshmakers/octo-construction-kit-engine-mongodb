using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using Persistence.SystemCkModel;

namespace Meshmakers.Octo.SystematizedData.Persistence.CkModelEntities;

[CkId(SystemCkModel.SystemCkModelId, SystemCkModel.SystemAutoIncrementCkId)]
public class RtSystemAutoIncrement : RtEntity
{
    public long? Start
    {
        get => GetAttributeValueOrDefault<long>(nameof(Start));
        set => SetAttributeValue(nameof(Start), AttributeValueTypes.Int, value);
    }

    public long? End
    {
        get => GetAttributeValueOrDefault<long>(nameof(End), long.MaxValue);
        set => SetAttributeValue(nameof(End), AttributeValueTypes.Int, value);
    }

    public long? CurrentValue
    {
        get => GetAttributeValueOrDefault(nameof(CurrentValue), Start);
        set => SetAttributeValue(nameof(CurrentValue), AttributeValueTypes.Int, value);
    }
}
