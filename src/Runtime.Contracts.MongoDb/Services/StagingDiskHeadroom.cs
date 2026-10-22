namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;

/// <summary>
/// Whether the storage nodes have room for the archive data of a restore below CrateDB's own disk watermarks
/// (AB#7274): the answer of <see cref="IStreamDataRepositoryFactory.CheckStagingDiskHeadroomAsync"/>.
/// </summary>
/// <param name="IsSufficient">Every node can take its share without crossing the high watermark.</param>
/// <param name="StagedBytes">Bytes of the staged (gzip-compressed) archive files the estimate starts from.</param>
/// <param name="ExpectedBytes">Bytes the load is expected to need over all nodes and copies (staged bytes times the size factor times the copies).</param>
/// <param name="Nodes">One entry per storage node; empty when the nodes could not be asked.</param>
/// <param name="Reason">
/// English text without any secret that says what does not fit (needed, available and watermark per refused node), or
/// that the check could not be made; <c>null</c> when sufficient.
/// </param>
public sealed record StagingDiskHeadroom(
    bool IsSufficient, long StagedBytes, long ExpectedBytes, IReadOnlyList<NodeDiskHeadroom> Nodes, string? Reason)
{
    /// <summary>The answer of a backend that has no disk to ask about: nothing is refused.</summary>
    public static StagingDiskHeadroom NotChecked(long stagedBytes)
        => new(true, stagedBytes, 0, [], null);
}

/// <summary>The room of one storage node (AB#7274).</summary>
/// <param name="Node">Name of the node.</param>
/// <param name="NeededBytes">Bytes this node is expected to take.</param>
/// <param name="AvailableBytes">Free bytes of the node's data volume.</param>
/// <param name="RoomBytes">
/// Bytes the node can still take before it crosses the high watermark; can be negative when it is above it already.
/// </param>
/// <param name="Watermark">The watermark in effect, e.g. <c>90%</c> or <c>100gb</c>.</param>
public sealed record NodeDiskHeadroom(string Node, long NeededBytes, long AvailableBytes, long RoomBytes, string Watermark)
{
    /// <summary>True when the node can take <see cref="NeededBytes"/>.</summary>
    public bool Fits => NeededBytes <= RoomBytes;
}
