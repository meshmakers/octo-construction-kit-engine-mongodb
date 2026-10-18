using System.Collections.Concurrent;
using Meshmakers.Octo.Runtime.Engine.CrateDb.QueryBuilder;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// What a process remembers about the recompute side of its rollups between calls of
/// <see cref="CrateDbArchiveRecomputeExecutor"/> (AB#6283). The executor itself does not live long
/// enough to remember anything: the stream data repository that owns it is created with every
/// tenant context, and a tenant context is created per request and per orchestrator tick. This
/// object is held by the singleton repository factory and handed to every executor it leads to.
/// </summary>
/// <remarks>
/// <para>
/// Every entry is keyed by the schema-qualified name of the rollup's live table, which is what the
/// state is about: it carries the tenant's schema (instance prefix included) and the archive's
/// rtId, so two tenants, and two archives of one schema, never share an entry.
/// </para>
/// <para>
/// The state describes tables, so it has to go when the tables go: <see cref="Forget"/> is called
/// wherever this process drops an archive's tables (<see cref="ArchiveTableDrop"/>). A drop by
/// another process is not seen here; the executor's "table is gone" path covers the staging table,
/// and the other entries only cost a repeated <c>CREATE TABLE IF NOT EXISTS</c> or reclaim pass
/// they would otherwise have saved.
/// </para>
/// <para>
/// Thread safety: the dictionaries are concurrent; the values of one rollup are only written by the
/// one recompute that may run on it at a time.
/// </para>
/// </remarks>
internal sealed class RecomputeProcessState
{
    /// <summary>The staging table as this process last left it: created with which statement, and whether it exists.</summary>
    internal readonly record struct StagingTable(string CreateSql, bool Exists);

    /// <summary>Staging tables this process has created. No entry ⇒ never touched by this process.</summary>
    public ConcurrentDictionary<string, StagingTable> StagingTables { get; } = new(StringComparer.Ordinal);

    /// <summary>Rollups whose pointer table this process has ensured.</summary>
    public ConcurrentDictionary<string, bool> EnsuredGenMaps { get; } = new(StringComparer.Ordinal);

    /// <summary>What a process last read of a tenant's restore reconcile marker (AB#6968), keyed by marker table and tenant id.</summary>
    internal readonly record struct MarkerAnswer(bool Pending, DateTime ExpiresUtc);

    /// <summary>The remembered answers about the restore reconcile marker; short-lived, see <see cref="Reconcile.RestoreReconcileMarker.CacheTtl"/>.</summary>
    public ConcurrentDictionary<string, MarkerAnswer> RestoreMarkers { get; } = new(StringComparer.Ordinal);

    /// <summary>Rollups whose pending-sweep record table this process has ensured (AB#6968).</summary>
    public ConcurrentDictionary<string, bool> EnsuredSweepTables { get; } = new(StringComparer.Ordinal);

    /// <summary>Rollups whose pointer entries were all checked for dead rows by this process.</summary>
    public ConcurrentDictionary<string, bool> ReclaimPassDone { get; } = new(StringComparer.Ordinal);

    /// <summary>Pointer entries whose sweep failed and still has to be made up for.</summary>
    public ConcurrentDictionary<string, ConcurrentQueue<GenerationRange>> PendingReclaims { get; } =
        new(StringComparer.Ordinal);

    /// <summary>
    /// Drops everything remembered about the rollup with the given live table — its tables are gone.
    /// </summary>
    public void Forget(string qualifiedLiveTable)
    {
        StagingTables.TryRemove(qualifiedLiveTable, out _);
        EnsuredGenMaps.TryRemove(qualifiedLiveTable, out _);
        EnsuredSweepTables.TryRemove(qualifiedLiveTable, out _);
        ReclaimPassDone.TryRemove(qualifiedLiveTable, out _);
        PendingReclaims.TryRemove(qualifiedLiveTable, out _);
    }
}
