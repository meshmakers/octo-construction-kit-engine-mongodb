using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// Pure-function generator for archive-table DDL. Produces deterministic CrateDB
/// <c>CREATE TABLE</c> and <c>DROP TABLE</c> statements from a pre-resolved set of
/// <see cref="ArchiveColumnDdl"/> entries plus the standard time-series columns and the
/// <c>(timestamp, rtId, ckTypeId)</c> primary key. No I/O — input goes in, SQL string comes out.
/// </summary>
internal static class ArchiveDdlGenerator
{
    /// <summary>
    /// Standard columns emitted on every raw or rollup archive table (concept §4). Lower-cased to
    /// sidestep CrateDB's case-preservation quirks for quoted mixed-case identifiers (notably
    /// <c>EXCLUDED."Col"</c> inside <c>ON CONFLICT DO UPDATE</c>). PK columns are <c>NOT NULL</c>
    /// by definition; timestamp columns default to <c>CURRENT_TIMESTAMP</c> for the standard trio.
    /// Time-range archives use <see cref="WindowedStandardColumns"/> instead.
    /// </summary>
    public static IReadOnlyList<(string Name, string Definition)> StandardColumns { get; } = new (string, string)[]
    {
        (Constants.RtId, "TEXT NOT NULL"),
        (Constants.Timestamp, "TIMESTAMP WITH TIME ZONE NOT NULL"),
        (Constants.CkTypeId, "TEXT NOT NULL"),
        (Constants.RtCreationDateTime, "TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP"),
        (Constants.RtChangedDateTime, "TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP"),
        (Constants.RtWellKnownName, "TEXT"),
    };

    /// <summary>
    /// Standard columns for archive tables that store half-open <c>[window_start, window_end)</c>
    /// windows — both <c>TimeRangeArchive</c> (external pre-aggregated data) and
    /// <c>RollupArchive</c> (system-orchestrated bucket aggregation, Phase 7 unification, concept-
    /// time-range §6). The natural primary key is <c>(window_start, window_end, rtid, ckTypeId)</c>;
    /// the <c>was_updated</c> flag is set to TRUE on every ON CONFLICT upsert so dashboards can
    /// detect retro-corrections without log-diving.
    /// </summary>
    public static IReadOnlyList<(string Name, string Definition)> WindowedStandardColumns { get; } = new (string, string)[]
    {
        (Constants.WindowStart, "TIMESTAMP WITH TIME ZONE NOT NULL"),
        (Constants.WindowEnd, "TIMESTAMP WITH TIME ZONE NOT NULL"),
        (Constants.RtId, "TEXT NOT NULL"),
        (Constants.CkTypeId, "TEXT NOT NULL"),
        (Constants.RtCreationDateTime, "TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP"),
        (Constants.RtChangedDateTime, "TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP"),
        (Constants.RtWellKnownName, "TEXT"),
        (Constants.WasUpdated, "BOOLEAN NOT NULL DEFAULT FALSE"),
    };

    /// <summary>
    /// Builds <c>CREATE TABLE IF NOT EXISTS {table} ( … ) CLUSTERED INTO N SHARDS [WITH (number_of_replicas = M)]</c>
    /// for a raw or rollup archive. <paramref name="qualifiedTableName"/> is expected to be
    /// already quoted (e.g. <c>"acmecorp"."tempSensor_telemetry"</c>); callers obtain it via
    /// <see cref="TenantSchema.QualifiedLegacyTable"/> or the equivalent archive helper.
    /// </summary>
    /// <param name="qualifiedTableName">Already-quoted schema-qualified table identifier.</param>
    /// <param name="columns">Archive-defined columns (after path resolution).</param>
    /// <param name="numberOfShards">
    /// Number of shards for the <c>CLUSTERED INTO</c> clause. Must be ≥ 1.
    /// </param>
    /// <param name="numberOfReplicas">
    /// Number of replicas. Negative ⇒ omit the clause entirely (CrateDB picks the cluster default).
    /// </param>
    public static string GenerateCreateTable(
        string qualifiedTableName,
        IReadOnlyList<ArchiveColumnDdl> columns,
        int numberOfShards,
        int numberOfReplicas)
    {
        if (string.IsNullOrWhiteSpace(qualifiedTableName))
        {
            throw new ArgumentException("qualifiedTableName must not be empty", nameof(qualifiedTableName));
        }
        if (numberOfShards < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(numberOfShards), "numberOfShards must be >= 1");
        }

        var seenColumnNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, _) in StandardColumns)
        {
            seenColumnNames.Add(name);
        }

        var sb = new StringBuilder();
        sb.Append("CREATE TABLE IF NOT EXISTS ").Append(qualifiedTableName).Append(" (");

        foreach (var (name, definition) in StandardColumns)
        {
            sb.Append(' ').Append('"').Append(name).Append("\" ").Append(definition).Append(',');
        }

        foreach (var col in columns)
        {
            var columnName = ResolveColumnName(col);
            if (!seenColumnNames.Add(columnName))
            {
                throw new ArgumentException(
                    $"Column name '{columnName}' collides with another archive column or a standard column.",
                    nameof(columns));
            }

            AppendColumn(sb, columnName, col);
            sb.Append(',');
        }

        sb.Append($" PRIMARY KEY (\"{Constants.Timestamp}\", \"{Constants.RtId}\", \"{Constants.CkTypeId}\")");
        sb.Append(") CLUSTERED INTO ")
          .Append(numberOfShards.ToString(CultureInfo.InvariantCulture))
          .Append(" SHARDS");

        if (numberOfReplicas >= 0)
        {
            sb.Append(" WITH (number_of_replicas = ")
              .Append(numberOfReplicas.ToString(CultureInfo.InvariantCulture))
              .Append(')');
        }

        sb.Append(';');
        return sb.ToString();
    }

    /// <summary>
    /// Builds <c>CREATE TABLE IF NOT EXISTS …</c> for any archive flavor that uses the windowed
    /// storage shape: <c>TimeRangeArchive</c> (external pre-aggregated data) or <c>RollupArchive</c>
    /// (system-orchestrated bucket aggregation, Phase 7 unification). Emits the
    /// <c>(window_start, window_end, rtid, ckTypeId)</c> primary key, the <c>was_updated</c> flag
    /// column, plus the user-defined data columns. Same shard/replica knobs as the raw overload.
    /// </summary>
    /// <param name="includeGeneration">
    /// When true (rollup archives only, AB#4184 Phase 6) the table gains a
    /// <c>generation BIGINT NOT NULL DEFAULT 0</c> column and the primary key is extended with it, so
    /// a recompute's new-generation rows coexist with the previous generation until the active-
    /// generation pointer flips. Time-range archives pass <c>false</c> and are unaffected.
    /// </param>
    public static string GenerateCreateWindowedTable(
        string qualifiedTableName,
        IReadOnlyList<ArchiveColumnDdl> columns,
        int numberOfShards,
        int numberOfReplicas,
        bool includeGeneration = false)
    {
        if (string.IsNullOrWhiteSpace(qualifiedTableName))
        {
            throw new ArgumentException("qualifiedTableName must not be empty", nameof(qualifiedTableName));
        }
        if (numberOfShards < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(numberOfShards), "numberOfShards must be >= 1");
        }

        var seenColumnNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, _) in WindowedStandardColumns)
        {
            seenColumnNames.Add(name);
        }

        var sb = new StringBuilder();
        sb.Append("CREATE TABLE IF NOT EXISTS ").Append(qualifiedTableName).Append(" (");

        foreach (var (name, definition) in WindowedStandardColumns)
        {
            sb.Append(' ').Append('"').Append(name).Append("\" ").Append(definition).Append(',');
        }

        if (includeGeneration)
        {
            sb.Append(" \"").Append(Constants.Generation).Append("\" BIGINT NOT NULL DEFAULT 0,");
            seenColumnNames.Add(Constants.Generation);
        }

        foreach (var col in columns)
        {
            var columnName = ResolveColumnName(col);
            if (!seenColumnNames.Add(columnName))
            {
                throw new ArgumentException(
                    $"Column name '{columnName}' collides with another archive column or a standard column.",
                    nameof(columns));
            }

            AppendColumn(sb, columnName, col);
            sb.Append(',');
        }

        var generationPkSuffix = includeGeneration ? $", \"{Constants.Generation}\"" : string.Empty;
        sb.Append($" PRIMARY KEY (\"{Constants.WindowStart}\", \"{Constants.WindowEnd}\", \"{Constants.RtId}\", \"{Constants.CkTypeId}\"{generationPkSuffix})");
        sb.Append(") CLUSTERED INTO ")
          .Append(numberOfShards.ToString(CultureInfo.InvariantCulture))
          .Append(" SHARDS");

        if (numberOfReplicas >= 0)
        {
            sb.Append(" WITH (number_of_replicas = ")
              .Append(numberOfReplicas.ToString(CultureInfo.InvariantCulture))
              .Append(')');
        }

        sb.Append(';');
        return sb.ToString();
    }

    /// <summary>
    /// Builds <c>ALTER TABLE {table} ADD COLUMN "{name}" {type} [INDEX OFF];</c> for adding a single
    /// column to an existing archive table — used when a computed column is added to an already-active
    /// archive (concept §8, Phase 7). Computed columns are nullable, so the column is never emitted
    /// with <c>NOT NULL</c> in practice; the generic Required/Indexed handling is preserved so the
    /// method works for any <see cref="ArchiveColumnDdl"/>.
    /// </summary>
    public static string GenerateAddColumn(string qualifiedTableName, ArchiveColumnDdl column)
    {
        if (string.IsNullOrWhiteSpace(qualifiedTableName))
        {
            throw new ArgumentException("qualifiedTableName must not be empty", nameof(qualifiedTableName));
        }

        var columnName = ResolveColumnName(column);

        var sb = new StringBuilder();
        sb.Append("ALTER TABLE ").Append(qualifiedTableName).Append(" ADD COLUMN");
        AppendColumn(sb, columnName, column);
        sb.Append(';');
        return sb.ToString();
    }

    /// <summary>
    /// Resolves the physical column name: the explicit <see cref="ArchiveColumnDdl.ColumnName"/> when
    /// set (computed columns), otherwise the camelCase name derived from
    /// <see cref="ArchiveColumnDdl.Path"/> via <see cref="ColumnNameMapper"/> (ingested / rollup
    /// columns). Two distinct paths can collapse to the same column name (e.g. <c>temp.celsius</c> and
    /// <c>tempCelsius</c>); the create generators surface that as a hard error rather than silently
    /// overwriting.
    /// </summary>
    internal static string ResolveColumnName(ArchiveColumnDdl col)
    {
        if (!string.IsNullOrWhiteSpace(col.ColumnName))
        {
            return col.ColumnName!;
        }

        if (string.IsNullOrWhiteSpace(col.Path))
        {
            throw new ArgumentException(
                "Archive column must have either a Path or an explicit ColumnName.", nameof(col));
        }

        return ColumnNameMapper.PathToColumnName(col.Path);
    }

    /// <summary>
    /// Appends <c>"name" TYPE [NOT NULL] [INDEX OFF]</c> (no trailing comma) for a single column.
    /// </summary>
    private static void AppendColumn(StringBuilder sb, string columnName, ArchiveColumnDdl col)
    {
        sb.Append(' ').Append('"').Append(columnName).Append("\" ");
        col.Type.AppendTo(sb);
        if (col.Required) sb.Append(" NOT NULL");
        if (!col.Indexed) sb.Append(" INDEX OFF");
    }

    /// <summary>
    /// Builds <c>DROP TABLE IF EXISTS {table};</c> for the given quoted, schema-qualified table.
    /// </summary>
    public static string GenerateDropTable(string qualifiedTableName)
    {
        if (string.IsNullOrWhiteSpace(qualifiedTableName))
        {
            throw new ArgumentException("qualifiedTableName must not be empty", nameof(qualifiedTableName));
        }
        return "DROP TABLE IF EXISTS " + qualifiedTableName + ";";
    }
}
