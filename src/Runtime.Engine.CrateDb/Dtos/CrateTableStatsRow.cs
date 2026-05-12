namespace Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos;

/// <summary>
/// One row of a `sys.shards` + `sys.health` join, materialised by Dapper from the bulk-stats query.
/// Backend-specific (CrateDB column names); the repository translates this onto the generic
/// <c>ArchiveStorageStats</c> contract before crossing back into <c>Runtime.Engine.MongoDb</c>.
/// </summary>
/// <param name="TableName">Per-archive table name without the schema qualifier (e.g. <c>archive_69fda707…</c>).</param>
/// <param name="Health">CrateDB health string (<c>GREEN</c> / <c>YELLOW</c> / <c>RED</c>) or null if not in <c>sys.health</c>.</param>
/// <param name="NumDocs">Sum of <c>num_docs</c> across all primary shards.</param>
/// <param name="SizeBytes">Sum of <c>size</c> across all primary shards.</param>
public sealed record CrateTableStatsRow(
    string TableName,
    string? Health,
    long NumDocs,
    long SizeBytes);
