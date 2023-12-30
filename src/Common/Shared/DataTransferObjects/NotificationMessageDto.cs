
// ReSharper disable PropertyCanBeMadeInitOnly.Global

namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

/// <summary>
///     Represents a notification message.
/// </summary>
public class NotificationMessageDto : RtEntityDto
{
    /// <summary>
    ///     Gets or sets the subject text.
    /// </summary>
    public string SubjectText { get; set; } = null!;

    /// <summary>
    ///     Gets or sets the body text.
    /// </summary>
    public string? BodyText { get; set; }

    /// <summary>
    ///     Gets or sets the recipient address.
    /// </summary>
    public string RecipientAddress { get; set; } = null!;

    /// <summary>
    ///     Gets or sets the notification type.
    /// </summary>
    public NotificationTypesDto? NotificationType { get; set; }

    /// <summary>
    ///     Get or sets the sent date time.
    /// </summary>
    public DateTime? SentDateTime { get; set; }

    /// <summary>
    ///     Gets or sets the date time of the last try to send.
    /// </summary>
    public DateTime? LastTryDateTime { get; set; }

    /// <summary>
    ///     Gets or sets the send status.
    /// </summary>
    public SendStatusDto? SendStatus { get; set; }

    /// <summary>
    ///     Gets or sets the error text which is set if the send status is <see cref="SendStatusDto.Error" />.
    /// </summary>
    public string? ErrorText { get; set; }
}