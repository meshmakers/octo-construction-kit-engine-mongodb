namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

public class NotificationTemplateDto : RtEntityDto
{
    public string? SubjectTemplate { get; set; }
    public string? BodyTemplate { get; set; }
    public NotificationTypesDto? Type { get; set; }
}