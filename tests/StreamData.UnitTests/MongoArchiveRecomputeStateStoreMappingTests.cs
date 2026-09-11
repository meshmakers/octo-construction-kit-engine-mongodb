using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.StreamData.Generated.System.StreamData.v1;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.MongoDb.StreamData;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// AB#5189: the pending recompute-range work list is settled by value — the orchestrator removes an
/// obligation by handing back the very record it read — so the record ↔ contract mapping in
/// <see cref="MongoArchiveRecomputeStateStore"/> must be an exact round trip, and a range written
/// before the attempt fields existed (System.StreamData &lt; 1.9.0) must read back as a fresh
/// obligation rather than fail or park.
/// </summary>
public class MongoArchiveRecomputeStateStoreMappingTests
{
    private static readonly OctoObjectId ArchiveRtId = new("aa00000000000000000003a0");
    private static readonly OctoObjectId Entity = new("aa00000000000000000003b0");
    private static readonly DateTime Start = new(2026, 5, 11, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime End = new(2026, 5, 11, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Enqueued = new(2026, 5, 11, 12, 30, 15, 250, DateTimeKind.Utc);

    [Fact]
    public void PreAttemptTrackingRecord_ReadsBackAsAFreshObligation()
    {
        // The shape a range had before 1.9.0: no attempt fields at all.
        var record = new RtCkArchiveRecomputeRangeRecord
        {
            DependentArchiveRtId = ArchiveRtId.ToString(),
            RangeStart = Start,
            RangeEnd = End,
            RtIdScope = string.Empty,
            EnqueuedAt = Enqueued,
        };

        var range = MongoArchiveRecomputeStateStore.FromRecord(record);

        Assert.Equal(0, range.Attempts);
        Assert.Null(range.NextAttemptAt);
        Assert.Null(range.LastError);
        Assert.Null(range.RtIdScope);
        Assert.True(range.IsDueAt(Start), "a pre-upgrade obligation is due on the very next tick");
        Assert.False(range.IsParked);
    }

    [Fact]
    public void RoundTrip_PreservesEveryField_SoAReadRangeRemovesItselfByValue()
    {
        var backedOff = new ArchiveRecomputeRange(ArchiveRtId, Start, End, Entity, Enqueued,
            3, Enqueued.AddMinutes(20), "CrateDB unhealthy");
        var parked = new ArchiveRecomputeRange(ArchiveRtId, Start, End, null, Enqueued,
            5, ArchiveRecomputeRange.ParkedUntil, "permanently broken");
        var fresh = new ArchiveRecomputeRange(ArchiveRtId, Start, End, null, Enqueued);

        Assert.Equal(backedOff, MongoArchiveRecomputeStateStore.FromRecord(MongoArchiveRecomputeStateStore.ToRecord(backedOff)));
        Assert.Equal(parked, MongoArchiveRecomputeStateStore.FromRecord(MongoArchiveRecomputeStateStore.ToRecord(parked)));
        Assert.Equal(fresh, MongoArchiveRecomputeStateStore.FromRecord(MongoArchiveRecomputeStateStore.ToRecord(fresh)));
        Assert.True(MongoArchiveRecomputeStateStore.FromRecord(MongoArchiveRecomputeStateStore.ToRecord(parked)).IsParked);
    }

    [Fact]
    public async Task Update_RemovesExactlyTheHandedBackRecords_AndAppendsTheNewOnes()
    {
        var a = new ArchiveRecomputeRange(ArchiveRtId, Start, End, null, Enqueued);
        var b = new ArchiveRecomputeRange(ArchiveRtId, End, End.AddHours(2), null, Enqueued, 2, Enqueued.AddMinutes(10), "boom");
        // Same obligation as a, enqueued later — a manual trigger coalesced while a was running.
        // The run that settled a cannot have honoured it, so it must survive.
        var aAgain = a with { EnqueuedAt = Enqueued.AddMinutes(1) };
        var remainder = b with { RangeStart = End.AddHours(1), Attempts = 3, LastError = "boom again" };
        var fake = FakeRepositoryOver(a, b, aAgain);

        await new MongoArchiveRecomputeStateStore(fake.Repository)
            .UpdatePendingRecomputeRangesAsync(ArchiveRtId, remove: new[] { a, b }, add: new[] { remainder });

        var after = fake.Persisted().PendingRecomputeRanges!.Select(MongoArchiveRecomputeStateStore.FromRecord).ToList();
        Assert.Equal(new[] { aAgain, remainder }, after);
    }

    [Fact]
    public async Task Update_WithNothingToRemoveOrAdd_DoesNotTouchTheEntity()
    {
        var fake = FakeRepositoryOver(new ArchiveRecomputeRange(ArchiveRtId, Start, End, null, Enqueued));

        await new MongoArchiveRecomputeStateStore(fake.Repository).UpdatePendingRecomputeRangesAsync(
            ArchiveRtId, Array.Empty<ArchiveRecomputeRange>(), Array.Empty<ArchiveRecomputeRange>());

        A.CallTo(() => fake.Repository.UpdateOneRtEntityByIdAsync(
                A<IOctoSession>._, A<RtCkId<CkTypeId>>._, A<OctoObjectId>._, A<RtEntity>._))
            .MustNotHaveHappened();
    }

    /// <summary>
    /// A faked tenant repository holding one rollup archive with the given work list; the accessor
    /// yields the entity as it was handed to the repository's update.
    /// </summary>
    private static (ITenantRepository Repository, Func<RtArchive> Persisted) FakeRepositoryOver(
        params ArchiveRecomputeRange[] pending)
    {
        var list = new AttributeRecordValueList<RtCkArchiveRecomputeRangeRecord>();
        list.AddRange(pending.Select(MongoArchiveRecomputeStateStore.ToRecord));
        var entity = new RtRollupArchive
        {
            RtId = ArchiveRtId,
            CkTypeId = new RtCkId<CkTypeId>("System.StreamData", new CkTypeId("RollupArchive")),
            PendingRecomputeRanges = list,
        };

        var repository = A.Fake<ITenantRepository>();
        A.CallTo(() => repository.GetSessionAsync()).Returns(A.Fake<IOctoSession>());
        A.CallTo(() => repository.GetRtEntityByRtIdAsync<RtArchive>(A<IOctoSession>._, ArchiveRtId))
            .Returns(entity);
        RtArchive? persisted = null;
        A.CallTo(() => repository.UpdateOneRtEntityByIdAsync(
                A<IOctoSession>._, A<RtCkId<CkTypeId>>._, ArchiveRtId, A<RtEntity>._))
            .Invokes((IOctoSession _, RtCkId<CkTypeId> _, OctoObjectId _, RtEntity e) => persisted = (RtArchive)e);

        return (repository, () => persisted ?? throw new InvalidOperationException("The store never persisted the entity."));
    }
}
