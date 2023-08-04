using Meshmakers.Common.Shared;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Common.Shared.DataTransferObjects;
using Meshmakers.Octo.Common.Shared.Services;
using Meshmakers.Octo.SystematizedData.Persistence.CkModelEntities;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemStores;

public class EntityNotificationRepository : INotificationRepository
{
    private readonly ISystemContext _systemContext;

    public EntityNotificationRepository(ISystemContext systemContext)
    {
        _systemContext = systemContext;
    }

    public async Task AddShortMessageAsync(string tenantId, string toPhoneNumber, string message)
    {
        await AddShortMessageAsync(tenantId, toPhoneNumber, message, null);
    }

    public async Task AddEMailMessageAsync(string tenantId, string emailAddress, string? subject,
        string? htmlMessage)
    {
        await AddEMailMessageAsync(tenantId, emailAddress, subject, htmlMessage, null);
    }

    public async Task AddShortMessageAsync(string tenantId, string toPhoneNumber, string message,
        RtEntityId? associatedRtId)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);
        ArgumentValidation.ValidateString(nameof(toPhoneNumber), toPhoneNumber);
        ArgumentValidation.ValidateString(nameof(message), message);

        try
        {
            var notificationMessage = new NotificationMessageDto
            {
                SendStatus = SendStatusDto.Pending,
                BodyText = message,
                RecipientAddress = toPhoneNumber,
                NotificationType = NotificationTypesDto.Sms,
                LastTryDateTime = DateTime.MinValue
            };

            await AddMessageAsync(tenantId, notificationMessage, associatedRtId);
        }
        catch (Exception e)
        {
            throw new NotificationSendFailedException("Message send failed.", e);
        }
    }

    public async Task AddEMailMessageAsync(string tenantId, string emailAddress, string? subject,
        string? htmlMessage, RtEntityId? associatedRtId)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);
        ArgumentValidation.ValidateString(nameof(emailAddress), emailAddress);

        try
        {
            var notificationMessage = new NotificationMessageDto
            {
                SendStatus = SendStatusDto.Pending,
                SubjectText = subject,
                BodyText = htmlMessage,
                RecipientAddress = emailAddress,
                NotificationType = NotificationTypesDto.EMail,
                LastTryDateTime = DateTime.MinValue
            };

            await AddMessageAsync(tenantId, notificationMessage, associatedRtId);
        }
        catch (Exception e)
        {
            throw new NotificationSendFailedException("Message send failed.", e);
        }
    }

    public async Task<PagedResult<NotificationMessageDto>> GetPendingMessagesAsync(string tenantId,
        NotificationTypesDto notificationType, int? take = null)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);

        var tenantRepository = await _systemContext.CreateOrGetTenantRepositoryAsync(tenantId);
        var session = await tenantRepository.StartSessionAsync();
        session.StartTransaction();

        var result = await tenantRepository.GetRtEntitiesByTypeAsync<RtSystemNotificationMessage>(session,
            new DataQueryOperation
            {
                FieldFilters = new[]
                {
                    new FieldFilter(nameof(RtSystemNotificationMessage.SendStatus), FieldFilterOperator.Equals,
                        SendStatusDto.Pending),
                    new FieldFilter(nameof(RtSystemNotificationMessage.LastTryDateTime),
                        FieldFilterOperator.LessEqualThan, DateTime.UtcNow.AddMinutes(-5)),
                    new FieldFilter(nameof(RtSystemNotificationMessage.NotificationType), FieldFilterOperator.Equals,
                        notificationType)
                }
            });

        await session.CommitTransactionAsync();

        return new PagedResult<NotificationMessageDto>(result.Result.Select(CreateNotificationMessage), 0, take,
            result.TotalCount);
    }

    public async Task<IEnumerable<NotificationMessageDto>> StoreNotificationMessages(string tenantId,
        IEnumerable<NotificationMessageDto> notificationMessages)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);


        var tenantRepository = await _systemContext.CreateOrGetTenantRepositoryAsync(tenantId);
        var session = await tenantRepository.StartSessionAsync();
        session.StartTransaction();

        var entityUpdateInfos = await Task.WhenAll(notificationMessages.Select(async dto =>
            new EntityUpdateInfo(await PrepareUpdateRtEntityAsync(session, dto, tenantRepository),
                EntityModOptions.Update)));

        await tenantRepository.ApplyChanges(session, entityUpdateInfos);

        await session.CommitTransactionAsync();

        return entityUpdateInfos.Select(x => CreateNotificationMessage((RtSystemNotificationMessage)x.RtEntity));
    }


    private async Task AddMessageAsync(string tenantId, NotificationMessageDto notificationMessageDto,
        RtEntityId? targetRtId)
    {
        ArgumentValidation.ValidateString(nameof(tenantId), tenantId);

        var tenantRepository = await _systemContext.CreateOrGetTenantRepositoryAsync(tenantId);
        var session = await tenantRepository.StartSessionAsync();
        session.StartTransaction();

        var rtEntity = CreateRtEntity(notificationMessageDto, tenantRepository);

        var associationUpdateInfos = new List<AssociationUpdateInfo>();
        if (targetRtId != null)
        {
            associationUpdateInfos.Add(new AssociationUpdateInfo(rtEntity.ToRtEntityId(), targetRtId.Value,
                NotificationCkModel.RelatedRoleId, AssociationModOptionsDto.Create));
        }

        await tenantRepository.ApplyChanges(session, new[]
        {
            new EntityUpdateInfo(rtEntity, EntityModOptions.Create)
        }, associationUpdateInfos);

        await session.CommitTransactionAsync();
    }

    private static RtSystemNotificationMessage CreateRtEntity(NotificationMessageDto notificationMessageDto,
        ITenantRepository tenantContext)
    {
        var rtEntity = tenantContext.CreateTransientRtEntity<RtSystemNotificationMessage>();

        ApplyDtoData(notificationMessageDto, rtEntity);

        return rtEntity;
    }

    private static void ApplyDtoData(NotificationMessageDto notificationMessageDto,
        RtSystemNotificationMessage rtEntity)
    {
        rtEntity.SubjectText = notificationMessageDto.SubjectText;
        rtEntity.BodyText = notificationMessageDto.BodyText;
        rtEntity.RecipientAddress = notificationMessageDto.RecipientAddress;
        rtEntity.SentDateTime = notificationMessageDto.SentDateTime;
        rtEntity.LastTryDateTime = notificationMessageDto.LastTryDateTime;
        rtEntity.ErrorText = notificationMessageDto.ErrorText;
        rtEntity.SendStatus = notificationMessageDto.SendStatus == null
            ? SendStatus.Pending
            : (SendStatus)notificationMessageDto.SendStatus;
        rtEntity.NotificationType = notificationMessageDto.NotificationType == null
            ? NotificationTypes.EMail
            : (NotificationTypes)notificationMessageDto.NotificationType;
    }

    private static async Task<RtSystemNotificationMessage> PrepareUpdateRtEntityAsync(IOctoSession session,
        NotificationMessageDto notificationMessageDto,
        ITenantRepository tenantRepository)
    {
        var rtEntity =
            await tenantRepository.GetRtEntityByRtIdAsync<RtSystemNotificationMessage>(session,
                notificationMessageDto.RtId);

        ApplyDtoData(notificationMessageDto, rtEntity);

        return rtEntity;
    }

    private static NotificationMessageDto CreateNotificationMessage(RtSystemNotificationMessage rtEntity)
    {
        return new NotificationMessageDto
        {
            RtId = rtEntity.RtId,
            CkId = rtEntity.CkId,
            SubjectText = rtEntity.SubjectText,
            BodyText = rtEntity.BodyText,
            RecipientAddress = rtEntity.RecipientAddress,
            SentDateTime = rtEntity.SentDateTime,
            LastTryDateTime = rtEntity.LastTryDateTime,
            SendStatus = (SendStatusDto?)rtEntity.SendStatus,
            NotificationType = (NotificationTypesDto?)rtEntity.NotificationType,
            ErrorText = rtEntity.ErrorText
        };
    }
}
