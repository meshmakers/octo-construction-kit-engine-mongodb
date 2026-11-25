using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;

using FluentAssertions;

using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using TestCkModel.Generated.Test.v1;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
///     Opt-in delete events of a runtime-entity watch (<see cref="WatchStreamFilter.IncludeDeleteEvents" />) against a
///     real MongoDB change stream. Without the opt-in a watch delivers exactly what it delivered before (no delete
///     events at all, whatever the update types say). With it a delete arrives with the document key's RtId; it is
///     matched on the pre-image when the collection captures one (exact for derived types sharing the collection), on
///     the document key only for a watch on the whole collection, and refused otherwise.
/// </summary>
/// <remarks>
///     The change stream cursor opens in the background after the watch call returns. Every test therefore waits for a
///     probe change of its own to arrive before it makes the changes under test (<see cref="EventLog{T}.ProbeAsync" />),
///     and a change stream delivers in commit order: once an event of a later change has arrived, an event the stream
///     would have delivered for an earlier change has arrived too. A negative assertion is made after such a later
///     event, never after a fixed wait.
/// </remarks>
[Collection(ImportTestCkModelCollection.Name)]
public class WatchRtEntitiesDeleteEventTests(ImportTestCkModelFixture fixture)
{
    private static readonly TimeSpan EventWaitTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ProbeRetryInterval = TimeSpan.FromMilliseconds(250);

    private ITenantRepository Repository => fixture.GetSystemContext().GetTenantRepository();

    private async Task<T> InsertAsync<T>(Action<T>? set = null) where T : RtEntity, new()
    {
        var entity = await Repository.CreateTransientRtEntityAsync<T>();
        set?.Invoke(entity);
        using var session = await Repository.GetSessionAsync();
        session.StartTransaction();
        await Repository.InsertOneRtEntityAsync(session, entity);
        await session.CommitTransactionAsync();
        return entity;
    }

    private async Task DeleteAsync<T>(T entity) where T : RtEntity, new()
    {
        using var session = await Repository.GetSessionAsync();
        session.StartTransaction();
        await Repository.DeleteOneRtEntityByRtIdAsync<T>(session, entity.RtId, DeleteOptions.Erase);
        await session.CommitTransactionAsync();
    }

    private async Task UpdateNameAsync<T>(T entity, string name) where T : RtEntity, new()
    {
        var rtEntityId = entity.ToRtEntityId();
        using var session = await Repository.GetSessionAsync();
        session.StartTransaction();
        await Repository.ApplyChangesAsync(session,
        [
            EntityUpdateInfo<RtEntity>.CreateUpdate(rtEntityId,
                new RtEntity(rtEntityId.CkTypeId, entity.RtId, new Dictionary<string, object?> { ["Name"] = name }))
        ], new OperationResult());
        await session.CommitTransactionAsync();
    }

    /// <summary>
    ///     Every event of a watch from the moment it is created: one subscription for the whole test (the stream stops
    ///     when an event finds no subscriber), and each wait sees all events so far.
    /// </summary>
    private sealed class EventLog<T> : IDisposable where T : class, new()
    {
        private readonly ReplaySubject<IUpdateInfo<T>> _events = new();
        private readonly HashSet<OctoObjectId> _probeIds = [];
        private readonly IDisposable _subscription;

        public EventLog(IUpdateStream<T> stream)
        {
            _subscription = stream.GetUpdates().Subscribe(_events);
        }

        /// <summary>Every event so far, probe events included.</summary>
        public IReadOnlyList<IUpdateInfo<T>> AllEvents
        {
            get
            {
                // A ReplaySubject replays synchronously on subscribe.
                var all = new List<IUpdateInfo<T>>();
                using (_events.Subscribe(all.Add))
                {
                }

                return all;
            }
        }

        /// <summary>The events so far, without the probe events.</summary>
        public IReadOnlyList<IUpdateInfo<T>> Events =>
            AllEvents.Where(e => e.DocumentRtId == null || !_probeIds.Contains(e.DocumentRtId.Value)).ToList();

        public async Task<IUpdateInfo<T>> WaitForAsync(Func<IUpdateInfo<T>, bool> predicate, TimeSpan timeout)
        {
            return await _events.Where(predicate).FirstAsync().Timeout(timeout).ToTask();
        }

        /// <summary>
        ///     Makes probe changes until the stream delivered the event of one: from then on the cursor is open. Each
        ///     probe is a change of its own (<paramref name="probe" /> returns its RtId); probe events are left out of
        ///     <see cref="Events" />.
        /// </summary>
        public async Task ProbeAsync(Func<Task<OctoObjectId>> probe, UpdateTypes probeEvent)
        {
            var deadline = DateTime.UtcNow + EventWaitTimeout;
            while (true)
            {
                var rtId = await probe();
                _probeIds.Add(rtId);
                try
                {
                    await WaitForAsync(e => e.UpdateType == probeEvent && e.DocumentRtId == rtId, ProbeRetryInterval);
                    return;
                }
                catch (TimeoutException) when (DateTime.UtcNow < deadline)
                {
                    // The cursor was not open yet when this probe was written; the next probe tells.
                }
            }
        }

        public void Dispose()
        {
            _subscription.Dispose();
            _events.Dispose();
        }
    }

    private Func<Task<OctoObjectId>> InsertProbe<T>(Action<T>? set = null) where T : RtEntity, new() =>
        async () => (await InsertAsync(set)).RtId;

    private Func<Task<OctoObjectId>> InsertAndDeleteProbe<T>(Action<T>? set = null) where T : RtEntity, new() =>
        async () =>
        {
            var probe = await InsertAsync(set);
            await DeleteAsync(probe);
            return probe.RtId;
        };

    // ---- without the opt-in: exactly today's events ----------------------------------------------------------

    // Each set of update types includes Insert, so an inserted probe shows that the cursor is open and, inserted after
    // the deletes, that the deletes went by. Delete alone takes the same path (no opt-in -> no delete branch) but would
    // deliver no event at all to wait for.
    [Theory]
    [InlineData(UpdateTypes.Insert | UpdateTypes.Delete)]
    [InlineData(UpdateTypes.Insert | UpdateTypes.Update | UpdateTypes.Replace | UpdateTypes.Delete)]
    [InlineData(UpdateTypes.Undefined)]
    public async Task WithoutTheOptIn_ADelete_DeliversNoEvent_WhateverTheUpdateTypesSay(UpdateTypes updateTypes)
    {
        var ct = TestContext.Current.CancellationToken;
        var withPreImage = await InsertAsync<RtWatchTarget>();
        var withoutPreImage = await InsertAsync<RtWatchTargetNoPreImage>();
        using var preImageLog = new EventLog<RtWatchTarget>(await Repository.WatchRtEntitiesAsync<RtWatchTarget>(
            new WatchStreamFilter { UpdateTypes = updateTypes }, ct));
        using var keyLog = new EventLog<RtWatchTargetNoPreImage>(
            await Repository.WatchRtEntitiesAsync<RtWatchTargetNoPreImage>(
                new WatchStreamFilter { UpdateTypes = updateTypes }, ct));
        await preImageLog.ProbeAsync(InsertProbe<RtWatchTarget>(), UpdateTypes.Insert);
        await keyLog.ProbeAsync(InsertProbe<RtWatchTargetNoPreImage>(), UpdateTypes.Insert);

        await DeleteAsync(withPreImage);
        await DeleteAsync(withoutPreImage);
        var lastPreImage = await InsertAsync<RtWatchTarget>();
        var lastKey = await InsertAsync<RtWatchTargetNoPreImage>();
        await preImageLog.WaitForAsync(e => e.DocumentRtId == lastPreImage.RtId, EventWaitTimeout);
        await keyLog.WaitForAsync(e => e.DocumentRtId == lastKey.RtId, EventWaitTimeout);

        preImageLog.Events.Should().NotContain(e => e.UpdateType == UpdateTypes.Delete);
        keyLog.Events.Should().NotContain(e => e.UpdateType == UpdateTypes.Delete);
    }

    // ---- document key: a watch on the whole collection -------------------------------------------------------

    [Fact]
    public async Task OptIn_OnACollectionRootWithoutPreImages_DeliversTheDeleteWithItsRtId()
    {
        var ct = TestContext.Current.CancellationToken;
        var entity = await InsertAsync<RtWatchTargetNoPreImage>(e => e.Name = "to delete");
        using var log = new EventLog<RtWatchTargetNoPreImage>(
            await Repository.WatchRtEntitiesAsync<RtWatchTargetNoPreImage>(
                new WatchStreamFilter { UpdateTypes = UpdateTypes.Delete, IncludeDeleteEvents = true }, ct));
        await log.ProbeAsync(InsertAndDeleteProbe<RtWatchTargetNoPreImage>(), UpdateTypes.Delete);

        await DeleteAsync(entity);

        var evt = await log.WaitForAsync(e => e.DocumentRtId == entity.RtId, EventWaitTimeout);
        evt.UpdateType.Should().Be(UpdateTypes.Delete);
        evt.Document.Should().BeNull();
    }

    [Fact]
    public async Task OptIn_WithAWatchedRtId_DeliversOnlyTheDeleteOfThatEntity()
    {
        var ct = TestContext.Current.CancellationToken;
        var watched = await InsertAsync<RtWatchTargetNoPreImage>();
        var other = await InsertAsync<RtWatchTargetNoPreImage>();
        // Update events of the watched entity are the probe: no other entity passes the RtId filter.
        using var log = new EventLog<RtWatchTargetNoPreImage>(
            await Repository.WatchRtEntitiesAsync<RtWatchTargetNoPreImage>(
                new WatchStreamFilter
                {
                    UpdateTypes = UpdateTypes.Update | UpdateTypes.Delete,
                    IncludeDeleteEvents = true,
                    RtId = watched.RtId
                }, ct));
        var probes = 0;
        await log.ProbeAsync(async () =>
        {
            await UpdateNameAsync(watched, $"probe-{probes++}");
            return watched.RtId;
        }, UpdateTypes.Update);

        await DeleteAsync(other);
        await DeleteAsync(watched);
        await log.WaitForAsync(e => e.UpdateType == UpdateTypes.Delete && e.DocumentRtId == watched.RtId,
            EventWaitTimeout);

        // The probe shares the watched RtId, so read the deletes from every event.
        log.AllEvents.Where(e => e.UpdateType == UpdateTypes.Delete).Select(e => e.DocumentRtId)
            .Should().Equal(watched.RtId);
    }

    [Fact]
    public async Task OptIn_KeepsTheOtherEventsAsBefore_AndTheyCarryTheirRtIdToo()
    {
        var ct = TestContext.Current.CancellationToken;
        using var log = new EventLog<RtWatchTargetNoPreImage>(
            await Repository.WatchRtEntitiesAsync<RtWatchTargetNoPreImage>(
                new WatchStreamFilter
                {
                    UpdateTypes = UpdateTypes.Insert | UpdateTypes.Delete, IncludeDeleteEvents = true
                }, ct));
        await log.ProbeAsync(InsertProbe<RtWatchTargetNoPreImage>(), UpdateTypes.Insert);

        var entity = await InsertAsync<RtWatchTargetNoPreImage>(e => e.Name = "new");
        await DeleteAsync(entity);
        await log.WaitForAsync(e => e.UpdateType == UpdateTypes.Delete && e.DocumentRtId == entity.RtId,
            EventWaitTimeout);

        var events = log.Events.Where(e => e.DocumentRtId == entity.RtId).ToList();
        events.Select(e => e.UpdateType).Should().Equal(UpdateTypes.Insert, UpdateTypes.Delete);
        events[0].Document!.RtId.Should().Be(entity.RtId);
    }

    [Fact]
    public async Task OptIn_OnADerivedTypeWithoutPreImages_IsRefused()
    {
        // Country shares the collection of its root Location with Continent and the other locations; the document key
        // cannot tell them apart, and Location captures no pre-images.
        var ct = TestContext.Current.CancellationToken;

        var act = async () => await Repository.WatchRtEntitiesAsync<RtCountry>(
            new WatchStreamFilter { UpdateTypes = UpdateTypes.Delete, IncludeDeleteEvents = true }, ct);

        var assertion = await act.Should().ThrowAsync<OperationFailedException>();
        assertion.Which.Message.Should().Contain("Location").And
            .Contain("EnableChangeStreamPreAndPostImages");
    }

    [Fact]
    public async Task OptIn_WithAFieldFilterWithoutPreImages_IsRefused()
    {
        var ct = TestContext.Current.CancellationToken;

        var act = async () => await Repository.WatchRtEntitiesAsync<RtWatchTargetNoPreImage>(
            new WatchStreamFilter
            {
                UpdateTypes = UpdateTypes.Delete,
                IncludeDeleteEvents = true,
                FieldFilterCriteria = FieldFilterCriteria.Create()
                    .Field(nameof(RtWatchTargetNoPreImage.Name), FieldFilterOperator.Equals, "x")
            }, ct);

        await act.Should().ThrowAsync<OperationFailedException>();
    }

    [Fact]
    public async Task WithoutTheOptIn_ADerivedTypeWithoutPreImages_StillWatchesAsBefore()
    {
        // The refusal belongs to the opt-in only: the same watch with Delete in its update types but without the
        // opt-in starts as it always did.
        var ct = TestContext.Current.CancellationToken;

        var act = async () =>
        {
            using var stream = await Repository.WatchRtEntitiesAsync<RtCountry>(
                new WatchStreamFilter { UpdateTypes = UpdateTypes.Insert | UpdateTypes.Delete }, ct);
        };

        await act.Should().NotThrowAsync();
    }

    // ---- pre-image: exact for derived types and filters ------------------------------------------------------

    [Fact]
    public async Task OptIn_WithPreImages_OnADerivedType_DeliversOnlyDeletesOfThatType_WithTheDeletedDocument()
    {
        var ct = TestContext.Current.CancellationToken;
        var baseEntity = await InsertAsync<RtWatchTarget>();
        var derived = await InsertAsync<RtWatchTargetDerived>(e => e.Name = "derived");
        using var log = new EventLog<RtWatchTargetDerived>(await Repository.WatchRtEntitiesAsync<RtWatchTargetDerived>(
            new WatchStreamFilter { UpdateTypes = UpdateTypes.Delete, IncludeDeleteEvents = true }, ct));
        await log.ProbeAsync(InsertAndDeleteProbe<RtWatchTargetDerived>(), UpdateTypes.Delete);

        await DeleteAsync(baseEntity);
        await DeleteAsync(derived);
        await log.WaitForAsync(e => e.DocumentRtId == derived.RtId, EventWaitTimeout);

        var evt = log.Events.Should().ContainSingle().Which;
        evt.DocumentRtId.Should().Be(derived.RtId);
        evt.DocumentBeforeChange!.RtId.Should().Be(derived.RtId);
    }

    [Fact]
    public async Task OptIn_WithPreImages_AppliesTheFieldFilterToTheDeletedDocument()
    {
        var ct = TestContext.Current.CancellationToken;
        var active = await InsertAsync<RtWatchTarget>(e => e.Status = "active");
        var inactive = await InsertAsync<RtWatchTarget>(e => e.Status = "inactive");
        using var log = new EventLog<RtWatchTarget>(await Repository.WatchRtEntitiesAsync<RtWatchTarget>(
            new WatchStreamFilter
            {
                UpdateTypes = UpdateTypes.Delete,
                IncludeDeleteEvents = true,
                FieldFilterCriteria = FieldFilterCriteria.Create()
                    .Field(nameof(RtWatchTarget.Status), FieldFilterOperator.Equals, "active")
            }, ct));
        await log.ProbeAsync(InsertAndDeleteProbe<RtWatchTarget>(e => e.Status = "active"), UpdateTypes.Delete);

        await DeleteAsync(inactive);
        await DeleteAsync(active);
        await log.WaitForAsync(e => e.DocumentRtId == active.RtId, EventWaitTimeout);

        log.Events.Select(e => e.DocumentRtId).Should().Equal(active.RtId);
    }
}
