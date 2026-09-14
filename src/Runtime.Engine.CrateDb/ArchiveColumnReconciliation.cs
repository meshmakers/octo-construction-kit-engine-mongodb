namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// Decides which declared archive columns the physical CrateDB table is missing.
/// </summary>
/// <remarks>
/// <para>
/// Archive tables are provisioned with <c>CREATE TABLE IF NOT EXISTS</c>, so on an archive that is
/// already activated provisioning is a no-op and a newly declared column never reaches CrateDB.
/// Whatever is written to it is then dropped by the storage layer as an unknown column — or, once
/// something references it in SQL, every write to the archive fails. Either way the archive
/// definition and the table silently disagree, and the only way to adopt the change used to be
/// dropping the table, which for a populated archive means losing its history.
/// </para>
/// <para>
/// Kept pure and separate from the repository so the rules below can be stated as tests rather than
/// inferred from a DDL trace.
/// </para>
/// </remarks>
internal static class ArchiveColumnReconciliation
{
    /// <summary>One column that has to be added to an existing table.</summary>
    /// <param name="Column">
    /// The column to add, already forced nullable: CrateDB cannot add a NOT NULL column to a table
    /// that may already hold rows, and every existing row necessarily has no value for it.
    /// </param>
    /// <param name="Name">Physical column name, for the DDL and the log line.</param>
    /// <param name="DeclaredRequired">
    /// True when the archive declares the column required. The addition still proceeds — leaving it
    /// out would preserve exactly the definition-versus-table disagreement this exists to close —
    /// but the caller warns, because the constraint the author asked for is not in effect.
    /// </param>
    internal sealed record Addition(ArchiveColumnDdl Column, string Name, bool DeclaredRequired);

    /// <summary>
    /// Returns the columns the archive declares that <paramref name="existingPhysicalColumns" />
    /// does not contain.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only additions are planned, deliberately. Dropping a column that is no longer declared would
    /// destroy data on a definition edit, and a type change is not expressible as an ALTER at all —
    /// both stay operator decisions rather than a side effect of re-activating an archive.
    /// </para>
    /// <para>
    /// Only <em>ingested</em> columns are considered: those identified by a CK attribute path and
    /// carrying no explicit column name. A computed column (explicit name) has its own lifecycle — a
    /// versioned physical column plus a backfill — and creating it empty here would bypass that and
    /// leave a column of NULLs that reads like data. A rollup's aggregate columns are excluded for
    /// the same reason: they are derived rather than written, and an empty one is indistinguishable
    /// from a bucket that legitimately aggregated to nothing.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<Addition> Plan(
        IReadOnlyList<ArchiveColumnDdl> resolvedColumns,
        IReadOnlySet<string> existingPhysicalColumns)
    {
        var additions = new List<Addition>();

        foreach (var column in resolvedColumns)
        {
            if (!string.IsNullOrEmpty(column.ColumnName) || string.IsNullOrEmpty(column.Path))
            {
                continue;
            }

            var name = ArchiveDdlGenerator.ResolveColumnName(column);
            if (existingPhysicalColumns.Contains(name))
            {
                continue;
            }

            additions.Add(new Addition(column with { Required = false }, name, column.Required));
        }

        return additions;
    }
}
