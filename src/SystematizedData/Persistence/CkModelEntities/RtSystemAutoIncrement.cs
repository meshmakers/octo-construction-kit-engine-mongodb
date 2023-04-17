using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using MongoDB.Bson.Serialization.Attributes;
using Newtonsoft.Json;

namespace Meshmakers.Octo.SystematizedData.Persistence.CkModelEntities;

[CkId(Constants.SystemAutoIncrementCkId)]
public class RtSystemAutoIncrement : RtEntity
{
    [JsonIgnore]
    [BsonIgnore]
    public long? Start
    {
        get => GetAttributeValueOrDefault<long>(nameof(Start));
        set => SetAttributeValue(nameof(Start), AttributeValueTypes.Int, value);
    }

    [JsonIgnore]
    [BsonIgnore]
    public long? End
    {
        get => GetAttributeValueOrDefault<long>(nameof(End), long.MaxValue);
        set => SetAttributeValue(nameof(End), AttributeValueTypes.Int, value);
    }

    [JsonIgnore]
    [BsonIgnore]
    public long? CurrentValue
    {
        get => GetAttributeValueOrDefault(nameof(CurrentValue), Start);
        set => SetAttributeValue(nameof(CurrentValue), AttributeValueTypes.Int, value);
    }
}
