using Meshmakers.Common.Shared;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.Common.Shared.DataTransferObjects;
using Meshmakers.Octo.Common.Shared.Exchange;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using Persistence.Contracts;
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
        var tenantContext = await _systemContext.CreateChildTenantContextAsync(tenantId);
        var tenantRepository = await tenantContext.CreateOrGetTenantRepositoryAsync();

        var session = await tenantRepository.StartSessionAsync();
        try
        {
            session.StartTransaction();

            var query = await tenantRepository.GetRtEntityByRtIdAsync(session,
                new RtEntityId(SystemCkModel.SystemCkModelId, SystemCkModel.SystemQueryCkId, queryId));

            if (CheckCancellation(cancellationToken))
            {
                throw new OperationCanceledException();
            }

            if (query == null)
            {
                throw new ModelExportException($"Query '{queryId}‘ does not exist.");
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

            var ckIdString = query.GetAttributeStringValueOrDefault("QueryCkId");
            if (string.IsNullOrWhiteSpace(ckIdString))
            {
                throw new ModelExportException($"Query '{queryId}‘ has no QueryCkId attribute set.");

            }
            var ckId = new CkId<CkTypeId>(ckIdString);

            var resultSet = await tenantRepository.GetRtEntitiesByTypeAsync(session, ckId, dataQueryOperation);

            var entityCacheItem = tenantRepository.GetEntityCacheItem(ckId);

            var model = new RtModelRoot();
            model.RtEntities.AddRange(resultSet.Items.Select(entity =>
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
            await RtSerializer.SerializeAsync(streamWriter, model);

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