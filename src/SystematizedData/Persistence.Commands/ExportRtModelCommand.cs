using Meshmakers.Common.Shared;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Common.Shared.DataTransferObjects;
using Meshmakers.Octo.Common.Shared.Exchange;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using Newtonsoft.Json;
using Persistence.SystemCkModel;

namespace Meshmakers.Octo.SystematizedData.Persistence.Commands;

public class ExportRtModelCommand : IExportRtModelCommand
{
    private readonly ISystemContext _systemContext;

    internal ExportRtModelCommand(ISystemContext systemContext)
    {
        _systemContext = systemContext;
    }


    public async Task ExportAsync(string tenantId, OctoObjectId queryId, string filePath,
        CancellationToken? cancellationToken)
    {
        var tenantRepository = await _systemContext.CreateOrGetTenantRepositoryAsync(tenantId);

        var session = await tenantRepository.StartSessionAsync();
        try
        {
            session.StartTransaction();

            var query = await tenantRepository.GetRtEntityByRtIdAsync(session,
                new RtEntityId(SystemCkModel.SystemQueryCkId, queryId));

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

            var resultSet = await tenantRepository.GetRtEntitiesByTypeAsync(session, ckId, dataQueryOperation);

            var entityCacheItem = tenantRepository.GetEntityCacheItem(ckId);

            var model = new RtModelRoot();
            model.RtEntities.AddRange(resultSet.Result.Select(entity =>
            {
                var exEntity = new RtEntity
                {
                    RtId = entity.RtId,
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
        catch (Exception e)
        {
            Console.WriteLine(e);
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