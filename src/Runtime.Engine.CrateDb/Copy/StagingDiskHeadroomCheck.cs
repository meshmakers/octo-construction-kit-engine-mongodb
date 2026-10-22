using System.Globalization;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb.Copy;

/// <summary>The disk of one storage node as CrateDB reports it (<c>sys.nodes</c>, <c>fs['total']</c>).</summary>
internal sealed record NodeDisk(string Name, long SizeBytes, long UsedBytes, long AvailableBytes);

/// <summary>
/// The high disk watermark of the cluster (<c>cluster.routing.allocation.disk.watermark.high</c>): CrateDB stops allocating
/// shards to a node that is above it and moves them away (AB#7274). A percentage (<c>90%</c>) or a ratio (<c>0.9</c>) is
/// the share of the disk that may be <em>used</em>, an absolute value (<c>100gb</c>) the free space that must <em>remain</em>.
/// </summary>
internal sealed record DiskWatermark(string Text, bool Enabled)
{
    /// <summary>The CrateDB default.</summary>
    public static readonly DiskWatermark Default = new("90%", true);

    /// <summary>Bytes the node can take before it reaches the watermark; negative when it is above it already.</summary>
    public long Room(NodeDisk node)
    {
        if (!Enabled)
        {
            return node.AvailableBytes;
        }

        if (TryParseUsedShare(Text, out var share))
        {
            return (long)(node.SizeBytes * share) - node.UsedBytes;
        }

        return TryParseBytes(Text, out var remain) ? node.AvailableBytes - remain : node.AvailableBytes - (long)(node.SizeBytes * 0.1);
    }

    /// <summary>"90%" and "0.9" (a ratio, as CrateDB accepts it) give 0.9.</summary>
    internal static bool TryParseUsedShare(string text, out double share)
    {
        share = 0;
        var t = text.Trim();
        if (t.EndsWith('%') && double.TryParse(t[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
        {
            share = percent / 100;
            return true;
        }

        if (!t.EndsWith('b') && double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var ratio) && ratio is >= 0 and <= 1)
        {
            share = ratio;
            return true;
        }

        return false;
    }

    /// <summary>"100gb", "512mb", "1tb", "10485760b" (case-insensitive).</summary>
    internal static bool TryParseBytes(string text, out long bytes)
    {
        bytes = 0;
        var t = text.Trim().ToLowerInvariant();
        (string Suffix, long Factor)[] units =
            [("pb", 1L << 50), ("tb", 1L << 40), ("gb", 1L << 30), ("mb", 1L << 20), ("kb", 1L << 10), ("b", 1)];
        foreach (var (suffix, factor) in units)
        {
            if (t.EndsWith(suffix, StringComparison.Ordinal) &&
                double.TryParse(t[..^suffix.Length], NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value >= 0)
            {
                bytes = (long)(value * factor);
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Decides whether the archive data of a load fits on the storage nodes below the high disk watermark (AB#7274), for one
/// archive (<see cref="StagingCopyRunner"/>, from its plan) and for a whole backup (the cluster-level check before a restore
/// changes anything). One implementation, so the two can never give different answers.
/// </summary>
internal static class StagingDiskHeadroomCheck
{
    /// <summary>Copies a table has after the load when the replica setting is not known: the default of the archive tables, <c>0-1</c>.</summary>
    internal const string DefaultReplicas = "0-1";

    public static async Task<(IReadOnlyList<NodeDisk> Nodes, DiskWatermark Watermark)> ReadAsync(
        ICrateCopyClient client, string tenantId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var nodes = (await client.ExecuteStatementAsync(tenantId, CopyStatement.Plain(
                "SELECT name, fs['total']['size'] AS size, fs['total']['used'] AS used, fs['total']['available'] AS available FROM sys.nodes",
                returnsRows: true), timeout, cancellationToken)).Rows
            .Select(r => new NodeDisk(Text(r, "name"), Long(r, "size"), Long(r, "used"), Long(r, "available")))
            .ToList();

        var watermark = DiskWatermark.Default;
        var setting = await client.ExecuteStatementAsync(tenantId, CopyStatement.Plain(
            "SELECT settings['cluster']['routing']['allocation']['disk']['watermark']['high'] AS high, " +
            "settings['cluster']['routing']['allocation']['disk']['threshold_enabled'] AS enabled FROM sys.cluster",
            returnsRows: true), timeout, cancellationToken);
        if (setting.Rows.Count > 0)
        {
            var high = Text(setting.Rows[0], "high");
            var enabled = !setting.Rows[0].TryGetValue("enabled", out var e) || e is not bool b || b;
            watermark = new DiskWatermark(high.Length > 0 ? high : DiskWatermark.Default.Text, enabled);
        }

        return (nodes, watermark);
    }

    /// <summary>
    /// Copies of every shard after the load: the tables are loaded without replicas and get their replica setting back
    /// (<c>0-1</c> = one replica as soon as a second node exists) before the segment merge, so the replicas count at the peak.
    /// </summary>
    public static int ReplicaCopies(string? replicas, int nodes)
    {
        var r = (replicas ?? DefaultReplicas).Trim();
        int copies;
        if (string.Equals(r, "0-all", StringComparison.OrdinalIgnoreCase))
        {
            copies = nodes;
        }
        else if (int.TryParse(r, NumberStyles.None, CultureInfo.InvariantCulture, out var fixedReplicas))
        {
            copies = 1 + fixedReplicas;
        }
        else if (r.Contains('-', StringComparison.Ordinal) &&
                 int.TryParse(r[(r.IndexOf('-', StringComparison.Ordinal) + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var max))
        {
            copies = 1 + max;
        }
        else
        {
            copies = 2;
        }

        return Math.Clamp(copies, 1, Math.Max(nodes, 1)); // replicas beyond the node count are never allocated
    }

    /// <summary>Pure: what every node needs and whether it fits.</summary>
    public static StagingDiskHeadroom Evaluate(
        long stagedBytes, int sizeFactor, string? replicas, IReadOnlyList<NodeDisk> nodes, DiskWatermark watermark, string subject)
    {
        var expected = stagedBytes * sizeFactor;
        var known = nodes.Where(n => n.SizeBytes > 0).ToList(); // a node that reports no disk size cannot be judged
        if (known.Count == 0)
        {
            return StagingDiskHeadroom.NotChecked(stagedBytes) with { ExpectedBytes = expected };
        }

        var copies = ReplicaCopies(replicas, nodes.Count);
        var perNode = (long)Math.Ceiling(expected * (double)copies / nodes.Count); // the shards spread evenly
        var report = known
            .Select(n => new NodeDiskHeadroom(n.Name, perNode, n.AvailableBytes, watermark.Room(n), watermark.Text))
            .ToList();
        var refused = report.Where(n => !n.Fits).OrderBy(n => n.RoomBytes).ToList();
        if (refused.Count == 0)
        {
            return new StagingDiskHeadroom(true, stagedBytes, expected, report, null);
        }

        var basis = $"{FormatBytes(perNode)} per node ({sizeFactor} times the {FormatBytes(stagedBytes)} of staged files, " +
                    $"{copies} cop{(copies == 1 ? "y" : "ies")} of every shard over {nodes.Count} node(s))";
        var details = string.Join("; ", refused.Select(n =>
            $"node '{n.Node}' has {FormatBytes(n.AvailableBytes)} available and room for {FormatBytes(Math.Max(n.RoomBytes, 0))} " +
            $"below the high disk watermark ({n.Watermark})"));
        var reason = $"{subject} needs about {basis}, but {details}.";
        return new StagingDiskHeadroom(false, stagedBytes, expected, report, reason);
    }

    internal static string FormatBytes(long bytes)
        => bytes >= 1L << 30 ? FormattableString.Invariant($"{bytes / (double)(1L << 30):0.0} GiB")
            : bytes >= 1L << 20 ? FormattableString.Invariant($"{bytes / (double)(1L << 20):0.0} MiB")
            : FormattableString.Invariant($"{bytes} bytes");

    private static long Long(IReadOnlyDictionary<string, object?> row, string column)
        => row.TryGetValue(column, out var value) && value is not null ? Convert.ToInt64(value, CultureInfo.InvariantCulture) : 0;

    private static string Text(IReadOnlyDictionary<string, object?> row, string column)
        => row.TryGetValue(column, out var value) && value is not null
            ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
            : string.Empty;
}
