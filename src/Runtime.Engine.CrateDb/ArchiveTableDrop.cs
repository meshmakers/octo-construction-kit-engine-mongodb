namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// The one place that knows which CrateDB tables an archive owns: its data table and, for rollups,
/// the generation-map side-table. Shared by the per-archive delete on the repository and the
/// tenant drop on the factory (AB#4255), so both drop exactly the same set - and never anything
/// else in the tenant's schema.
/// </summary>
internal static class ArchiveTableDrop
{
    /// <summary>
    /// Drops the archive table and the genmap side-table of <paramref name="archiveRtId"/> in the
    /// schema of <paramref name="tenantId"/>. Both statements are <c>DROP TABLE IF EXISTS</c>.
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
    }
}
