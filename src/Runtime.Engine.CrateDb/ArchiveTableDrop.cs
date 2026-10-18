namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// The one place that knows which CrateDB tables an archive owns: its data table and, for rollups,
/// the generation-map side-table, the recompute staging table and the pending-sweep record table. Shared by the per-archive delete on the repository and the
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
    /// <param name="managementClient">DDL client.</param>
    /// <param name="tenantId">The tenant whose schema holds the tables.</param>
    /// <param name="archiveRtId">The archive.</param>
    /// <param name="recomputeState">
    /// The process's recompute state, which remembers the staging and pointer table of a rollup; it
    /// forgets them here, so that an archive coming back under the same rtId starts from scratch.
    /// </param>
    public static async Task DropAsync(IStreamDataDatabaseManagementClient managementClient, string tenantId,
        string archiveRtId, RecomputeProcessState? recomputeState = null)
    {
        var qualifiedTable = TenantSchema.QualifiedArchiveTable(tenantId, archiveRtId);
        // Before the first statement: a drop that fails half-way must not leave the memo of a table
        // that may be gone.
        recomputeState?.Forget(qualifiedTable);
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

        // The pending-sweep record table (AB#6968) exists only for a rollup that was recomputed with a sweep
        // grace period above zero; IF EXISTS makes it a no-op for every other archive.
        var sweepTable = PendingSweepSqlBuilder.SweepTable(tenantId, archiveRtId);
        await managementClient.ExecuteDdlAsync(tenantId, PendingSweepSqlBuilder.BuildDropIfExists(sweepTable));
    }
}
