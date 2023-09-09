using Meshmakers.Common.Shared;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Common.Shared.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Serialization;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using Microsoft.Extensions.Logging;
using Persistence.SystemCkModel.ConstructionKit.Generated.System.v1;
using RtEntityDto = Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects.RtEntityDto;

namespace Meshmakers.Octo.SystematizedData.Persistence.Commands;

public class ExportRtModelCommand : IExportRtModelCommand
{
    private readonly ILogger<ExportRtModelCommand> _logger;
    private readonly ISystemContext _systemContext;

    internal ExportRtModelCommand(ILogger<ExportRtModelCommand> logger, ISystemContext systemContext)
    {
        _logger = logger;
        _systemContext = systemContext;
    }


    public async Task ExportAsync(string tenantId, OctoObjectId queryId, string filePath,
        CancellationToken? cancellationToken)
    {
        var tenantContext = await _systemContext.CreateChildTenantContextAsync(tenantId);
        var tenantRepository = await tenantContext.CreateOrGetTenantRepositoryAsync();

        var session = await tenantRepository.StartSessionAsync();
        try
        {
            session.StartTransaction();

            var query = await tenantRepository.GetRtEntityByRtIdAsync(session,
                new RtEntityId(SystemCkIds.ModelId, SystemCkIds.Query, queryId));

            if (CheckCancellation(cancellationToken))
            {
                throw new OperationCanceledException();
            }

            if (query == null)
            {
                throw CommandExecutionFailedException.QueryNotFound(queryId);
            }

            var dataQueryOperation = new DataQueryOperation();

            var sortingDtoList = query.GetAttributeStringValueOrDefault("Sorting")?.Deserialize<ICollection<SortDto>>();
            dataQueryOperation.SortOrders = sortingDtoList?.Select(dto =>
                new SortOrderItem(dto.AttributeName.ToPascalCase(), (SortOrders)dto.SortOrder)).ToList() ?? new List<SortOrderItem>();
            var fieldFilterDtoList =
                query.GetAttributeStringValueOrDefault("FieldFilter")?.Deserialize<ICollection<FieldFilterDto>>();
            dataQueryOperation.FieldFilters = fieldFilterDtoList?.Select(dto =>
                new FieldFilter(TransformAttributeName(dto.AttributeName), (FieldFilterOperator)dto.Operator,
                    dto.ComparisonValue)).ToList() ?? new List<FieldFilter>();

            var ckTypeIdString = query.GetAttributeStringValueOrDefault("QueryCkTypeId");
            if (string.IsNullOrWhiteSpace(ckTypeIdString))
            {
                throw CommandExecutionFailedException.QueryCkTypeIdNotSet(queryId);

            }
            var ckTypeId = new CkId<CkTypeId>(ckTypeIdString);

            var resultSet = await tenantRepository.GetRtEntitiesByTypeAsync(session, ckTypeId, dataQueryOperation);

            var entityCacheItem = tenantRepository.GetEntityCacheItem(ckTypeId);

            var model = new RtModelRootDto();
            model.RtEntities.AddRange(resultSet.Items.Select(entity =>
            {
                var exEntity = new RtEntityDto
                {
                    RtId = entity.RtId,
                    RtChangedDateTime = entity.RtChangedDateTime,
                    RtCreationDateTime = entity.RtCreationDateTime,
                    RtWellKnownName = entity.RtWellKnownName,
                    CkTypeId = entity.CkTypeId
                };

                exEntity.Attributes.AddRange(entity.Attributes.Select(pair =>
                {
                    var attributeCacheItem = entityCacheItem.Attributes[pair.Key];
                    return new RtAttributeDto
                    {
                        Id = attributeCacheItem.AttributeId,
                        Value = pair.Value
                    };
                }));

                return exEntity;
            }));

            await using var streamWriter = new StreamWriter(filePath);
            await RtSerializer.SerializeAsync(streamWriter, model);

            await session.CommitTransactionAsync();
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Exporting model failed");
            throw;
        }
    }

    private static string TransformAttributeName(string attributeNameDto)
    {
        var attributeName = attributeNameDto.ToPascalCase();


        return attributeName;
    }

    private static bool CheckCancellation(CancellationToken? cancellationToken)
    {
        if (cancellationToken != null && cancellationToken.Value.IsCancellationRequested)
        {
            return true;
        }

        return false;
    }
}