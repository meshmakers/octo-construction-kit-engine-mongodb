using System;

namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

public class NotificationMessageDto : RtEntityDto
{
    public string? SubjectText { get; set; }
    public string? BodyText { get; set; }
    public string? RecipientAddress { get; set; }

    public NotificationTypesDto? NotificationType { get; set; }

    public DateTime? SentDateTime { get; set; }

    public DateTime? LastTryDateTime { get; set; }

    public SendStatusDto? SendStatus { get; set; }

    public string? ErrorText { get; set; }
}
