using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

// Behavior pinned for IndexUsageClassifier — pure function that decides the Stage 3 /
// AB#4224 status of one index. Drives the Studio row colour and whether the drop button
// renders, so the boundary behavior matters.
public class IndexUsageClassifierTests
{
    private static IndexUsageEntry Entry(
        long ops = 0,
        int ageDays = 30,
        bool isBuiltin = false,
        string name = "x_1")
        => new(
            CollectionName: "rt_entities",
            IndexName: name,
            KeySpec: "{\"x\": 1}",
            OpsCount: ops,
            SinceUtc: DateTimeOffset.UtcNow.AddDays(-ageDays),
            AgeDays: ageDays,
            IsBuiltin: isBuiltin,
            DropShellCommand: isBuiltin ? null : $"db.rt_entities.dropIndex(\"{name}\")",
            Status: IndexUsageStatus.Used); // placeholder, classifier overwrites

    [Fact]
    public void Builtin_OverridesEverything()
    {
        // _id_ is Mongo-managed; even if it had non-zero ops (it always does), even if it's
        // ancient, the classification must be Builtin so the surface treats it as read-only.
        var entry = Entry(ops: 50_000_000, ageDays: 365, isBuiltin: true, name: "_id_");

        var status = IndexUsageClassifier.Classify(entry, minAgeDays: 7, lowUsageOpsThreshold: 10);

        Assert.Equal(IndexUsageStatus.Builtin, status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(6)]
    public void YoungerThanMinAge_AlwaysUsed_RegardlessOfOps(int ageDays)
    {
        // An index added an hour ago has had no chance to be queried; calling it Unused
        // would be a false-positive operator might act on. Anything younger than minAgeDays
        // is Used regardless of ops count (including 0).
        var entry = Entry(ops: 0, ageDays: ageDays);

        var status = IndexUsageClassifier.Classify(entry, minAgeDays: 7, lowUsageOpsThreshold: 10);

        Assert.Equal(IndexUsageStatus.Used, status);
    }

    [Fact]
    public void OldEnough_ZeroOps_Unused()
    {
        var entry = Entry(ops: 0, ageDays: 30);

        var status = IndexUsageClassifier.Classify(entry, minAgeDays: 7, lowUsageOpsThreshold: 10);

        Assert.Equal(IndexUsageStatus.Unused, status);
    }

    [Fact]
    public void OldEnough_BelowLowUsageThreshold_LowUsage()
    {
        var entry = Entry(ops: 5, ageDays: 30);

        var status = IndexUsageClassifier.Classify(entry, minAgeDays: 7, lowUsageOpsThreshold: 10);

        Assert.Equal(IndexUsageStatus.LowUsage, status);
    }

    [Fact]
    public void OldEnough_AtLowUsageThreshold_IsUsed()
    {
        // Threshold is strict "<": exactly at the threshold counts as Used.
        var entry = Entry(ops: 10, ageDays: 30);

        var status = IndexUsageClassifier.Classify(entry, minAgeDays: 7, lowUsageOpsThreshold: 10);

        Assert.Equal(IndexUsageStatus.Used, status);
    }

    [Fact]
    public void OldEnough_AboveLowUsageThreshold_Used()
    {
        var entry = Entry(ops: 50_000, ageDays: 30);

        var status = IndexUsageClassifier.Classify(entry, minAgeDays: 7, lowUsageOpsThreshold: 10);

        Assert.Equal(IndexUsageStatus.Used, status);
    }

    [Fact]
    public void ExactlyAtMinAge_IsEvaluated_NotPlaceholderUsed()
    {
        // ageDays == minAgeDays is the boundary. Off-by-one matters: at the threshold we
        // CAN evaluate (not still-too-young). With 0 ops at the threshold age, the answer
        // is Unused, not Used.
        var entry = Entry(ops: 0, ageDays: 7);

        var status = IndexUsageClassifier.Classify(entry, minAgeDays: 7, lowUsageOpsThreshold: 10);

        Assert.Equal(IndexUsageStatus.Unused, status);
    }
}
