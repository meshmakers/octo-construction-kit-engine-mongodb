using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Common.Shared.DataTransferObjects;
using Meshmakers.Octo.Common.Shared.Exchange;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using Newtonsoft.Json;

namespace Meshmakers.Octo.SystematizedData.Persistence.Commands;

internal class ExportRtModel
{
    private readonly ITenantContextInternal _tenantContext;

    internal ExportRtModel(ITenantContextInternal tenantContext)
    {
        _tenantContext = tenantContext;
    }

    public async Task Export(IOctoSession session, OctoObjectId queryId, string filePath,
        CancellationToken? cancellationToken)
    {
        var query = await _tenantContext.Repository.GetRtEntityAsync(session,
            new RtEntityId(Constants.SystemQueryCkId, queryId));
        if (CheckCancellation(cancellationToken))
        {
            throw new OperationCanceledException();
        }

        if (query == null)
        {
            throw new ModelExportException($"Query '{queryId}‘ does not exist.");
        }

        var dataQueryOperation = new DataQueryOperation();

        var sortingDtoList =
            JsonConvert.DeserializeObject<ICollection<SortDto>>(query.GetAttributeStringValueOrDefault("Sorting"));
        dataQueryOperation.SortOrders = sortingDtoList?.Select(dto =>
            new SortOrderItem(dto.AttributeName.ToPascalCase(), (SortOrders)dto.SortOrder));
        var fieldFilterDtoList =
            JsonConvert.DeserializeObject<ICollection<FieldFilterDto>>(
                query.GetAttributeStringValueOrDefault("FieldFilter"));
        dataQueryOperation.FieldFilters = fieldFilterDtoList?.Select(dto =>
            new FieldFilter(TransformAttributeName(dto.AttributeName), (FieldFilterOperator)dto.Operator,
                dto.ComparisonValue));

        var ckId = query.GetAttributeStringValueOrDefault("QueryCkId");

        var resultSet = await _tenantContext.Repository.GetRtEntitiesByTypeAsync(session, ckId, dataQueryOperation);

        var entityCacheItem = _tenantContext.CkCache.GetEntityCacheItem(ckId);

        var model = new RtModelRoot();
        model.RtEntities.AddRange(resultSet.Result.Select(entity =>
        {
            var exEntity = new RtEntity
            {
                RtId = entity.RtId.ToOctoObjectId(),
                RtChangedDateTime = entity.RtChangedDateTime,
                RtCreationDateTime = entity.RtCreationDateTime,
                RtWellKnownName = entity.RtWellKnownName,
                CkId = entity.CkId
            };

            exEntity.Attributes.AddRange(entity.Attributes.Select(pair =>
            {
                var attributeCacheItem = entityCacheItem.Attributes[pair.Key];
                return new RtAttribute
                {
                    Id = attributeCacheItem.AttributeId,
                    Value = pair.Value
                };
            }));

            return exEntity;
        }));

        await using var streamWriter = new StreamWriter(filePath);
        RtSerializer.Serialize(streamWriter, model);

        await session.CommitTransactionAsync();
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
