namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantOwnership;

/// <summary>
///     Identifies one ownership marker of a tenant database (AB#4945): the owning instance and the moment it stamped
///     the marker. A database another instance re-creates under the same name gets a new marker with a new stamp, so
///     the pair tells the marker a delete override was granted for apart from any later one (AB#8807).
/// </summary>
/// <param name="OwnerSystemDatabaseName">The normalized system database of the instance the marker names.</param>
/// <param name="StampedAtUtc">When that instance stamped the marker (UTC, millisecond precision as stored).</param>
public sealed record TenantOwnershipMarkerIdentity(string OwnerSystemDatabaseName, DateTime StampedAtUtc);
