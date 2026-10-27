using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;

/// <summary>
///     Result of <see cref="ITenantRepository.GetRtEntitiesByInterfaceAsync" /> (AB#7019): one page of the entities of
///     all implementers of a CK interface, in the global order of the query.
/// </summary>
/// <param name="Items">The entities of the requested page, in order. Untyped (<see cref="RtEntity" />); the
/// <see cref="RtEntity.CkTypeId" /> names the concrete type of every row.</param>
/// <param name="TotalCount">The number of entities that match the query over all collections, or <c>null</c> when
/// the caller did not ask for it (<c>includeTotalCount: false</c>). The count is a separate query per collection;
/// it costs one pass over the matching rows of the collections.</param>
public sealed record RtInterfaceQueryResult(IReadOnlyList<RtEntity> Items, long? TotalCount);
