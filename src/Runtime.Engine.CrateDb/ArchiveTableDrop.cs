namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// The one place that knows which CrateDB tables an archive owns: its data table and, for rollups,
/// the generation-map side-table and the recompute staging table. Shared by the per-archive delete on the repository and the
/// tenant drop on the factory (AB#4255), so both drop exactly the same set - and never anything
/// else in the tenant's schema.
/// </summary>
internal static class ArchiveTableDrop
{
    /// <summary>
    /// Drops the archive table, the genmap side-table and the recompute staging table of
    /// <paramref name="archiveRtId"/> in the schema of <paramref name="tenantId"/>. Every statement
    /// is <c>DROP TABLE IF EXISTS</c>.
    /// </summary>
    public static async Task DropAsync(IStreamDataDatabaseManagementClient managementClient, string tenantId,
        string archiveRtId)
    {
        var qualifiedTable = TenantSchema.QualifiedArchiveTable(tenantId, archiveRtId);
        await managementClient.ExecuteDdlAsync(tenantId, ArchiveDdlGenerator.GenerateDropTable(qualifiedTable));

        // Drop the Phase-6 generation-map side-table too (IF EXISTS — no-op for raw / time-range
        // archives that never had one).
        var genMapTable = GenerationMapSqlBuilder.GenMapTable(tenantId, archiveRtId);
        await managementClient.ExecuteDdlAsync(tenantId, GenerationMapSqlBuilder.BuildDropIfExists(genMapTable));

        // The recompute staging table of a rollup is kept between runs (AB#6283), so it lives as
        // long as its archive and goes with it (IF EXISTS — no-op for an archive that was never
        // recomputed, and for raw / time-range archives).
        var stagingTable = RollupRecomputeSqlBuilder.StagingTable(tenantId, archiveRtId);
        await managementClient.ExecuteDdlAsync(tenantId, RollupRecomputeSqlBuilder.BuildDropIfExists(stagingTable));
    }
}
