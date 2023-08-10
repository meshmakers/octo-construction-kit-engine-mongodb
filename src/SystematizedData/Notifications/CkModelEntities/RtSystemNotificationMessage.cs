using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using Meshmakers.Octo.SystematizedData.Persistence.SystemStores;

namespace Meshmakers.Octo.SystematizedData.Persistence.CkModelEntities;

[CkId(NotificationCkModel.SystemCkModelId, NotificationCkModel.SystemNotificationMessageTypeId)]
public class RtSystemNotificationMessage : RtEntity
{
    public string? SubjectText
    {
        get => GetAttributeStringValueOrDefault(nameof(SubjectText));
        set => SetAttributeValue(nameof(SubjectText), AttributeValueTypes.String, value);
    }

    public string? BodyText
    {
        get => GetAttributeStringValueOrDefault(nameof(BodyText));
        set => SetAttributeValue(nameof(BodyText), AttributeValueTypes.String, value);
    }

    public string? RecipientAddress
    {
        get => GetAttributeStringValueOrDefault(nameof(RecipientAddress));
        set => SetAttributeValue(nameof(RecipientAddress), AttributeValueTypes.String, value);
    }

    public NotificationTypes? NotificationType
    {
        get => GetAttributeValueOrDefault<NotificationTypes>(nameof(NotificationType));
        set => SetAttributeValue(nameof(NotificationType), AttributeValueTypes.Int, value);
    }

    public SendStatus? SendStatus
    {
        get => GetAttributeValueOrDefault<SendStatus>(nameof(SendStatus));
        set => SetAttributeValue(nameof(SendStatus), AttributeValueTypes.Int, value);
    }

    public DateTime? SentDateTime
    {
        get => GetAttributeValueOrDefault<DateTime>(nameof(SentDateTime));
        set => SetAttributeValue(nameof(SentDateTime), AttributeValueTypes.DateTime, value);
    }

    public DateTime? LastTryDateTime
    {
        get => GetAttributeValueOrDefault<DateTime>(nameof(LastTryDateTime));
        set => SetAttributeValue(nameof(LastTryDateTime), AttributeValueTypes.DateTime, value);
    }

    public string? ErrorText
    {
        get => GetAttributeStringValueOrDefault(nameof(ErrorText));
        set => SetAttributeValue(nameof(ErrorText), AttributeValueTypes.String, value);
    }
}
