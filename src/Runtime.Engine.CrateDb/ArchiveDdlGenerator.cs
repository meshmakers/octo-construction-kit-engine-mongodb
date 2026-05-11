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
    /// Standard columns emitted on every archive table (concept §4). Lower-cased to sidestep
    /// CrateDB's case-preservation quirks for quoted mixed-case identifiers (notably
    /// <c>EXCLUDED."Col"</c> inside <c>ON CONFLICT DO UPDATE</c>). PK columns are <c>NOT NULL</c>
    /// by definition; timestamp columns default to <c>CURRENT_TIMESTAMP</c> for the standard trio.
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
    /// Builds <c>CREATE TABLE IF NOT EXISTS {table} ( … ) CLUSTERED INTO N SHARDS [WITH (number_of_replicas = M)]</c>.
    /// <paramref name="qualifiedTableName"/> is expected to be already quoted (e.g.
    /// <c>"acmecorp"."tempSensor_telemetry"</c>); callers obtain it via
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
            if (string.IsNullOrWhiteSpace(col.Path))
            {
                throw new ArgumentException("Archive column path must not be empty.", nameof(columns));
            }

            // Convert dotted attribute path to a camelCase column identifier so per-archive
            // tables match the MongoDB BSON convention (concept §9). Two distinct paths can in
            // principle collapse to the same column name (e.g. `temp.celsius` and `tempCelsius`),
            // which we surface as a hard error rather than silently overwriting.
            var columnName = ColumnNameMapper.PathToColumnName(col.Path);
            if (!seenColumnNames.Add(columnName))
            {
                throw new ArgumentException(
                    $"Column name '{columnName}' (from path '{col.Path}') collides with another archive column or a standard column.",
                    nameof(columns));
            }

            sb.Append(' ').Append('"').Append(columnName).Append("\" ");
            col.Type.AppendTo(sb);
            if (col.Required) sb.Append(" NOT NULL");
            if (!col.Indexed) sb.Append(" INDEX OFF");
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
