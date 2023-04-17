using System;
using Meshmakers.Octo.Backend.Persistence.DatabaseEntities;
using MongoDB.Bson.Serialization.Attributes;
using Newtonsoft.Json;

namespace Meshmakers.Octo.Backend.Persistence.CkModelEntities;

[CkId(Constants.SystemNotificationMessageCkId)]
public class RtSystemNotificationMessage : RtEntity
{
    [JsonIgnore]
    [BsonIgnore]
    public string? SubjectText
    {
        get => GetAttributeStringValueOrDefault(nameof(SubjectText));
        set => SetAttributeValue(nameof(SubjectText), AttributeValueTypes.String, value);
    }

    [JsonIgnore]
    [BsonIgnore]
    public string? BodyText
    {
        get => GetAttributeStringValueOrDefault(nameof(BodyText));
        set => SetAttributeValue(nameof(BodyText), AttributeValueTypes.String, value);
    }

    [JsonIgnore]
    [BsonIgnore]
    public string? RecipientAddress
    {
        get => GetAttributeStringValueOrDefault(nameof(RecipientAddress));
        set => SetAttributeValue(nameof(RecipientAddress), AttributeValueTypes.String, value);
    }

    [JsonIgnore]
    [BsonIgnore]
    public NotificationTypes? NotificationType
    {
        get => GetAttributeValueOrDefault<NotificationTypes>(nameof(NotificationType));
        set => SetAttributeValue(nameof(NotificationType), AttributeValueTypes.Int, value);
    }

    [JsonIgnore]
    [BsonIgnore]
    public SendStatus? SendStatus
    {
        get => GetAttributeValueOrDefault<SendStatus>(nameof(SendStatus));
        set => SetAttributeValue(nameof(SendStatus), AttributeValueTypes.Int, value);
    }

    [JsonIgnore]
    [BsonIgnore]
    public DateTime? SentDateTime
    {
        get => GetAttributeValueOrDefault<DateTime>(nameof(SentDateTime));
        set => SetAttributeValue(nameof(SentDateTime), AttributeValueTypes.DateTime, value);
    }

    [JsonIgnore]
    [BsonIgnore]
    public DateTime? LastTryDateTime
    {
        get => GetAttributeValueOrDefault<DateTime>(nameof(LastTryDateTime));
        set => SetAttributeValue(nameof(LastTryDateTime), AttributeValueTypes.DateTime, value);
    }

    [JsonIgnore]
    [BsonIgnore]
    public string? ErrorText
    {
        get => GetAttributeStringValueOrDefault(nameof(ErrorText));
        set => SetAttributeValue(nameof(ErrorText), AttributeValueTypes.String, value);
    }
}
