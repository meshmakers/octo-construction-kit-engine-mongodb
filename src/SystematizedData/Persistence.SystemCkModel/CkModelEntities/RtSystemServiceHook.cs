using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using Persistence.SystemCkModel;

namespace Meshmakers.Octo.SystematizedData.Persistence.CkModelEntities;

[CkId(SystemCkModel.SystemCkModelId, SystemCkModel.SystemServiceHookCkId)]
public class RtSystemServiceHook : RtEntity
{
    public bool? Enabled
    {
        get => GetAttributeValueOrDefault<bool>(nameof(Enabled));
        set => SetAttributeValue(nameof(Enabled), AttributeValueTypes.Boolean, value);
    }

    public string? QueryCkId
    {
        get => GetAttributeStringValueOrDefault(nameof(QueryCkId));
        set => SetAttributeValue(nameof(QueryCkId), AttributeValueTypes.String, value);
    }

    public string? FieldFilter
    {
        get => GetAttributeStringValueOrDefault(nameof(FieldFilter));
        set => SetAttributeValue(nameof(FieldFilter), AttributeValueTypes.String, value);
    }

    public string? Name
    {
        get => GetAttributeStringValueOrDefault(nameof(Name));
        set => SetAttributeValue(nameof(Name), AttributeValueTypes.String, value);
    }

    public string? ServiceHookBaseUri
    {
        get => GetAttributeStringValueOrDefault(nameof(ServiceHookBaseUri));
        set => SetAttributeValue(nameof(ServiceHookBaseUri), AttributeValueTypes.String, value);
    }

    public string? ServiceHookAction
    {
        get => GetAttributeStringValueOrDefault(nameof(ServiceHookAction));
        set => SetAttributeValue(nameof(ServiceHookAction), AttributeValueTypes.String, value);
    }
}
