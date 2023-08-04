using System;
using System.Text.Json.Serialization;

namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

public class NotificationMessageInputDto
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? SubjectText { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? BodyText { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? RecipientAddress { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public NotificationTypesDto? NotificationType { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public DateTime? SentDateTime { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SendStatusDto? SendStatus { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public DateTime? LastTryDateTime { get; set; }


    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? ErrorText { get; set; }


    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public RtAssociationInputDto[]? RelatesTo { get; set; }
}
