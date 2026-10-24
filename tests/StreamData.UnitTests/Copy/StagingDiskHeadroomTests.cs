using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Copy;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests.Copy;

/// <summary>
/// AB#7274: the disk check honours CrateDB's high disk watermark (not the raw free space), counts the replicas that are
/// switched on again after the load, and is one rule for a single archive and for a whole backup.
/// </summary>
public class StagingDiskHeadroomTests
{
    private const long GiB = 1L << 30;

    private static readonly DiskWatermark Percent90 = new("90%", true);

    private static NodeDisk Node(string name, long size, long used) => new(name, size, used, size - used);

    // ---- the watermark ----------------------------------------------------------------------------------------

    [Theory]
    [InlineData("90%", 100, 80, 10)]      // may use 90 of 100 GiB, uses 80
    [InlineData("0.9", 100, 80, 10)]      // a ratio is the same thing
    [InlineData("85%", 100, 90, -5)]      // above the watermark already: negative room
    [InlineData("15gb", 100, 80, 5)]      // an absolute value is the free space that must remain: 20 free, 15 must stay
    [InlineData("1tb", 2000, 100, 876)]   // 1900 free, 1024 must stay
    public void Room_IsTheDistanceToTheHighWatermark(string watermark, long sizeGiB, long usedGiB, long expectedGiB)
    {
        var node = Node("n", sizeGiB * GiB, usedGiB * GiB);

        Assert.Equal(expectedGiB * GiB, new DiskWatermark(watermark, true).Room(node));
    }

    [Fact]
    public void Room_WithTheThresholdSwitchedOff_IsTheFreeSpace()
        => Assert.Equal(20 * GiB, new DiskWatermark("90%", false).Room(Node("n", 100 * GiB, 80 * GiB)));

    [Theory]
    [InlineData("0-1", 1, 1)]
    [InlineData("0-1", 2, 2)]
    [InlineData("0-1", 5, 2)]
    [InlineData("0-all", 3, 3)]
    [InlineData("1", 2, 2)]
    [InlineData("1", 1, 1)]
    [InlineData("0", 3, 1)]
    [InlineData("1-2", 4, 3)]
    [InlineData(null, 2, 2)]
    [InlineData("garbage", 4, 2)]
    public void ReplicaCopies_AreBoundedByTheNodes(string? setting, int nodes, int expected)
        => Assert.Equal(expected, StagingDiskHeadroomCheck.ReplicaCopies(setting, nodes));

    // ---- the evaluation ---------------------------------------------------------------------------------------

    [Fact]
    public void Evaluate_RefusesWhatFitsTheFreeSpaceButNotTheWatermark()
    {
        // 20 GiB free would take 12 GiB, but the node may only grow by 10 GiB before the high watermark
        var result = StagingDiskHeadroomCheck.Evaluate(1 * GiB, 12, "0", [Node("data-hot-1", 100 * GiB, 80 * GiB)], Percent90, "Loading X");

        Assert.False(result.IsSufficient);
        Assert.Equal(12 * GiB, result.ExpectedBytes);
        var node = Assert.Single(result.Nodes);
        Assert.Equal(12 * GiB, node.NeededBytes);
        Assert.Equal(20 * GiB, node.AvailableBytes);
        Assert.Equal(10 * GiB, node.RoomBytes);
        Assert.Equal("90%", node.Watermark);
        Assert.Contains("12.0 GiB per node", result.Reason);
        Assert.Contains("node 'data-hot-1' has 20.0 GiB available and room for 10.0 GiB below the high disk watermark (90%)", result.Reason);
    }

    [Fact]
    public void Evaluate_CountsTheReplicasOverTheNodes()
    {
        var nodes = new[] { Node("a", 100 * GiB, 0), Node("b", 100 * GiB, 0) };

        // 2 nodes with one replica: every node holds one full copy of the data
        Assert.Equal(12 * GiB, StagingDiskHeadroomCheck.Evaluate(1 * GiB, 12, "0-1", nodes, Percent90, "x").Nodes[0].NeededBytes);
        // the same without replicas: half of it
        Assert.Equal(6 * GiB, StagingDiskHeadroomCheck.Evaluate(1 * GiB, 12, "0", nodes, Percent90, "x").Nodes[0].NeededBytes);
        // one node cannot hold a replica
        Assert.Equal(12 * GiB, StagingDiskHeadroomCheck.Evaluate(1 * GiB, 12, "0-1", [nodes[0]], Percent90, "x").Nodes[0].NeededBytes);
    }

    [Fact]
    public void Evaluate_NamesEveryRefusedNode_TightestFirst_AndPassesWhenAllFit()
    {
        var tight = StagingDiskHeadroomCheck.Evaluate(2 * GiB, 12, "0", [Node("a", 40 * GiB, 30 * GiB), Node("b", 40 * GiB, 35 * GiB), Node("c", 400 * GiB, 0)], Percent90, "x");
        Assert.False(tight.IsSufficient);
        Assert.True(tight.Reason!.IndexOf("node 'b'", StringComparison.Ordinal) < tight.Reason.IndexOf("node 'a'", StringComparison.Ordinal));
        Assert.DoesNotContain("node 'c'", tight.Reason);

        var roomy = StagingDiskHeadroomCheck.Evaluate(1 * GiB, 12, "0", [Node("a", 400 * GiB, 30 * GiB)], Percent90, "x");
        Assert.True(roomy.IsSufficient);
        Assert.Null(roomy.Reason);
    }

    [Fact]
    public void Evaluate_IgnoresANodeThatReportsNoDiskSize()
    {
        Assert.True(StagingDiskHeadroomCheck.Evaluate(1 * GiB, 12, "0", [new NodeDisk("n", 0, 0, 0)], Percent90, "x").IsSufficient);

        var mixed = StagingDiskHeadroomCheck.Evaluate(1 * GiB, 12, "0", [new NodeDisk("n", 0, 0, 0), Node("m", 400 * GiB, 0)], Percent90, "x");
        Assert.True(mixed.IsSufficient);
        Assert.Equal("m", Assert.Single(mixed.Nodes).Node);
    }

    [Fact]
    public void Evaluate_WithoutNodes_RefusesNothing()
        => Assert.True(StagingDiskHeadroomCheck.Evaluate(1 * GiB, 12, "0", [], Percent90, "x").IsSufficient);

    // ---- one archive, against the simulated cluster ---------------------------------------------------------------

    private static readonly OctoObjectId Source = OctoObjectId.GenerateNewId();
    private static readonly OctoObjectId Restored = OctoObjectId.GenerateNewId();
    private static readonly ArchiveCopyTarget SourceTarget = new(Source, "tenant1", SimulatedCrate.SourceTable, "timestamp");
    private static readonly ArchiveCopyTarget RestoreTarget = new(Restored, "tenant1", SimulatedCrate.TargetTable, "timestamp");

    private static readonly ArchiveCopyStagingLocation Location = new(
        "s3://CANARYKEYID1234:CANARY/SECRET+value==@minio.octo-infra:9000/octo-artifacts/copy-staging/t1/run1",
        new Dictionary<string, string> { ["protocol"] = "http" });

    private static readonly ArchiveCopyOptions Small = new() { TargetSliceRows = 4 };
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly SimulatedCrate _crate = new();

    private async Task<ArchiveCopyPlan> DumpedWithOneGiB()
    {
        for (var h = 0; h < 10; h++) _crate.SourceRows.Add(new DateTime(2026, 3, 1, h, 0, 0, DateTimeKind.Utc));
        var runner = new StagingCopyRunner(NullLogger.Instance, "tenant1", _crate);
        var plan = await runner.PlanAsync(SourceTarget, Small, Ct);
        await foreach (var _ in runner.CopyToAsync(SourceTarget, plan, Location, Small, Ct))
        {
        }

        _crate.Executed.Clear();
        // exactly 1 GiB staged over all slices
        return plan with { Slices = plan.Slices.Select((s, i) => s.WithStagedBytes(i == 0 ? 1L * GiB : 0)).ToList() };
    }

    private async Task<ArchiveCopyRefusedException?> LoadAsync(ArchiveCopyPlan plan)
    {
        try
        {
            await foreach (var _ in new StagingCopyRunner(NullLogger.Instance, "tenant1", _crate).CopyFromAsync(RestoreTarget, plan, Location, Small, Ct))
            {
            }

            return null;
        }
        catch (ArchiveCopyRefusedException e)
        {
            return e;
        }
    }

    [Fact]
    public async Task Archive_ThatFitsTheFreeSpaceButNotTheWatermark_IsRefusedBeforeAnythingChanges()
    {
        var plan = await DumpedWithOneGiB();
        _crate.NodeDiskAvailable = [20 * GiB]; // 12 GiB needed, 20 GiB free ...
        _crate.NodeDiskUsed = [80 * GiB];      // ... but only 10 GiB below 90 % of 100 GiB
        _crate.Replicas = "0";

        var refused = await LoadAsync(plan);

        Assert.NotNull(refused);
        Assert.Equal(ArchiveCopyRefusalReason.InsufficientDiskSpace, refused.Reason);
        Assert.Contains("12.0 GiB per node", refused.Message);
        Assert.Contains("room for 10.0 GiB below the high disk watermark (90%)", refused.Message);
        Assert.Contains("Nothing was loaded.", refused.Message);
        Assert.DoesNotContain(_crate.Executed, s => s.StartsWith("ALTER", StringComparison.Ordinal));
        Assert.DoesNotContain(_crate.Executed, s => s.StartsWith("COPY", StringComparison.Ordinal));
        Assert.Empty(_crate.TargetRows);
    }

    [Fact]
    public async Task Archive_WithAnAbsoluteWatermark_KeepsThatMuchFree()
    {
        var plan = await DumpedWithOneGiB();
        _crate.NodeDiskAvailable = [20 * GiB];
        _crate.NodeDiskUsed = [80 * GiB];
        _crate.Replicas = "0";
        _crate.HighWatermark = "15gb"; // 5 GiB of room

        Assert.NotNull(await LoadAsync(plan));

        _crate.HighWatermark = "5gb"; // 15 GiB of room
        Assert.Null(await LoadAsync(plan));
    }

    [Fact]
    public async Task Archive_ThreeNodesAndAReplica_NeedsTwoThirdsOfTheDataOnEveryNode()
    {
        var plan = await DumpedWithOneGiB();
        _crate.NodeDiskAvailable = [9 * GiB, 9 * GiB, 9 * GiB]; // 8 GiB (2/3 of 12) fits below the watermark of an empty disk
        _crate.NodeDiskUsed = [0, 0, 0];
        _crate.Replicas = "0-1";

        Assert.Null(await LoadAsync(plan));
    }
}
