using Meshmakers.Octo.Common.Shared.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Common.Shared.Services;

/// <summary>
///     Interface for the notification repository.
/// </summary>
public interface INotificationRepository
{
    /// <summary>
    ///     Adds a notification message to the repository using short message service.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="toPhoneNumber">The receiver phone number</param>
    /// <param name="message">The message to be sent</param>
    /// <returns></returns>
    Task AddShortMessageAsync(string tenantId, string toPhoneNumber, string message);

    /// <summary>
    ///     Adds a notification message to the repository using email service.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="emailAddress">The receiver E-Mail address</param>
    /// <param name="subject">The subject of the E-Mail message</param>
    /// <param name="htmlMessage">The HTML formatted E-Mail body message</param>
    /// <returns></returns>
    Task AddEMailMessageAsync(string tenantId, string emailAddress, string subject, string? htmlMessage);

    /// <summary>
    ///     Adds a notification message to the repository using short message service.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="toPhoneNumber">The receiver phone number</param>
    /// <param name="message">The message to be sent</param>
    /// <param name="associatedRtId">The entity identifier the notification event is associated to.</param>
    /// <returns></returns>
    Task AddShortMessageAsync(string tenantId, string toPhoneNumber, string message, RtEntityId? associatedRtId);

    /// <summary>
    ///     Adds a notification message to the repository using email service.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="emailAddress">The receiver E-Mail address</param>
    /// <param name="subject">The subject of the E-Mail message</param>
    /// <param name="htmlMessage">The HTML formatted E-Mail body message</param>
    /// <param name="associatedRtId">The entity identifier the notification event is associated to.</param>
    /// <returns></returns>
    Task AddEMailMessageAsync(string tenantId, string emailAddress, string subject, string? htmlMessage,
        RtEntityId? associatedRtId);

    /// <summary>
    ///     Gets pending notification messages.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="notificationType">Type of notification, whether short message or E-Mail for example.</param>
    /// <param name="take">The amount of message to be received for paging</param>
    /// <returns></returns>
    Task<PagedResult<NotificationMessageDto>> GetPendingMessagesAsync(string tenantId,
        NotificationTypesDto notificationType, int? take = null);

    /// <summary>
    ///     Stores notification messages.
    /// </summary>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="notificationMessages">Notification message data transfer object to be updated.</param>
    /// <returns></returns>
    Task<IEnumerable<NotificationMessageDto>> StoreNotificationMessages(string tenantId,
        IEnumerable<NotificationMessageDto> notificationMessages);
}