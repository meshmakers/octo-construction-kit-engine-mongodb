using Meshmakers.Octo.Backend.Persistence.DatabaseEntities;
using MongoDB.Bson.Serialization.Attributes;
using Newtonsoft.Json;

namespace Meshmakers.Octo.Backend.Persistence.CkModelEntities;

[CkId(Constants.SystemNotificationTemplate)]
public class RtSystemNotificationTemplate : RtEntity
{
    [JsonIgnore]
    [BsonIgnore]
    public string? SubjectTemplate
    {
        get => GetAttributeStringValueOrDefault(nameof(SubjectTemplate));
        set => SetAttributeValue(nameof(SubjectTemplate), AttributeValueTypes.String, value);
    }

    [JsonIgnore]
    [BsonIgnore]
    public string? BodyTemplate
    {
        get => GetAttributeStringValueOrDefault(nameof(BodyTemplate));
        set => SetAttributeValue(nameof(BodyTemplate), AttributeValueTypes.String, value);
    }
}
