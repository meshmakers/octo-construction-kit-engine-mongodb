using Meshmakers.Octo.Backend.Persistence.DatabaseEntities;
using MongoDB.Bson.Serialization.Attributes;
using Newtonsoft.Json;

namespace Meshmakers.Octo.Backend.Persistence.CkModelEntities;

[CkId(Constants.SystemServiceHookCkId)]
public class RtSystemServiceHook : RtEntity
{
    [JsonIgnore]
    [BsonIgnore]
    public bool? Enabled
    {
        get => GetAttributeValueOrDefault<bool>(nameof(Enabled));
        set => SetAttributeValue(nameof(Enabled), AttributeValueTypes.Boolean, value);
    }

    [JsonIgnore]    
    [BsonIgnore]
    public string? QueryCkId
    {
        get => GetAttributeStringValueOrDefault(nameof(QueryCkId));
        set => SetAttributeValue(nameof(QueryCkId), AttributeValueTypes.String, value);
    }

    [JsonIgnore]
    [BsonIgnore]
    public string? FieldFilter
    {
        get => GetAttributeStringValueOrDefault(nameof(FieldFilter));
        set => SetAttributeValue(nameof(FieldFilter), AttributeValueTypes.String, value);
    }

    [JsonIgnore]
    public string? Name
    {
        get => GetAttributeStringValueOrDefault(nameof(Name));
        set => SetAttributeValue(nameof(Name), AttributeValueTypes.String, value);
    }

    [JsonIgnore]
    [BsonIgnore]
    public string? ServiceHookBaseUri
    {
        get => GetAttributeStringValueOrDefault(nameof(ServiceHookBaseUri));
        set => SetAttributeValue(nameof(ServiceHookBaseUri), AttributeValueTypes.String, value);
    }

    [JsonIgnore]
    [BsonIgnore]
    public string? ServiceHookAction
    {
        get => GetAttributeStringValueOrDefault(nameof(ServiceHookAction));
        set => SetAttributeValue(nameof(ServiceHookAction), AttributeValueTypes.String, value);
    }
}
