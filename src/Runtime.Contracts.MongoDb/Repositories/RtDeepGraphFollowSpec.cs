using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;

/// <summary>
///     A directed edge-following rule for the role-set deep-graph export (AB#5003): the deep graph
///     follows the given association role only in the given direction. Because each role is walked
///     in one direction, hub types are natural dead-ends — following <c>GrantsPermission</c> inbound
///     collects a permission's granting roles, and from a role there is no further inbound
///     <c>GrantsPermission</c> edge (a role is always the origin), so the closure stops instead of
///     spreading across the whole identity graph.
/// </summary>
/// <param name="RoleId">The association role to follow</param>
/// <param name="Direction">
///     <see cref="GraphDirections.Outbound" /> follows origin&#8594;target,
///     <see cref="GraphDirections.Inbound" /> follows target&#8594;origin.
/// </param>
public sealed record RtDeepGraphFollowSpec(RtCkId<CkAssociationRoleId> RoleId, GraphDirections Direction);
