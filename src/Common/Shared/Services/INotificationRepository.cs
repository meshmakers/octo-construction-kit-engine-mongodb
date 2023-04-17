using System.Collections.Generic;
using System.Threading.Tasks;
using Meshmakers.Octo.Common.Shared.DataTransferObjects;

namespace Meshmakers.Octo.Common.Shared.Services;

public interface INotificationRepository
{
    Task AddShortMessageAsync(string tenantId, string toPhoneNumber, string message);
    Task AddEMailMessageAsync(string tenantId, string emailAddress, string subject, string htmlMessage);

    Task AddShortMessageAsync(string tenantId, string toPhoneNumber, string message, RtEntityId? associatedRtId);

    Task AddEMailMessageAsync(string tenantId, string emailAddress, string? subject, string? htmlMessage,
        RtEntityId? associatedRtId);

    Task<PagedResult<NotificationMessageDto>> GetPendingMessagesAsync(string tenantId,
        NotificationTypesDto notificationType, int? take = null);

    Task<IEnumerable<NotificationMessageDto>> StoreNotificationMessages(string tenantId,
        IEnumerable<NotificationMessageDto> notificationMessages);
}
