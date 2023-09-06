using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using Meshmakers.Octo.SystematizedData.Persistence.SystemStores;

namespace Meshmakers.Octo.SystematizedData.Persistence.CkModelEntities;

[CkId(NotificationCkModel.SystemCkModelId, NotificationCkModel.SystemNotificationTemplateTypeId)]
public class RtSystemNotificationTemplate : RtEntity
{
    public string? SubjectTemplate
    {
        get => GetAttributeStringValueOrDefault(nameof(SubjectTemplate));
        set => SetAttributeValue(nameof(SubjectTemplate), AttributeValueTypes.String, value);
    }

    public string? BodyTemplate
    {
        get => GetAttributeStringValueOrDefault(nameof(BodyTemplate));
        set => SetAttributeValue(nameof(BodyTemplate), AttributeValueTypes.String, value);
    }
}
