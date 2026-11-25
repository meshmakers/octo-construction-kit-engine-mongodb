using System.Reactive.Linq;
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
[Collection(ImportTestCkModelCollection.Name)]
public class WatchRtEntitiesDeleteEventTests(ImportTestCkModelFixture fixture)
{
    private static readonly TimeSpan CursorWarmup = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan EventWaitTimeout = TimeSpan.FromSeconds(5);

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

    /// <summary>All events that arrive within <paramref name="window" />.</summary>
    private static async Task<IList<IUpdateInfo<T>>> CollectAsync<T>(IUpdateStream<T> stream, TimeSpan window)
    {
        try
        {
            return await stream.GetUpdates().Take(window).ToList().ToTask();
        }
        catch (TimeoutException)
        {
            return [];
        }
    }

    private static async Task<IUpdateInfo<T>?> FirstDeleteAsync<T>(IUpdateStream<T> stream, TimeSpan timeout)
    {
        try
        {
            return await stream.GetUpdates().Where(u => u.UpdateType == UpdateTypes.Delete).FirstAsync()
                .Timeout(timeout).ToTask();
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    // ---- without the opt-in: exactly today's events ----------------------------------------------------------

    [Theory]
    [InlineData(UpdateTypes.Delete)]
    [InlineData(UpdateTypes.Insert | UpdateTypes.Update | UpdateTypes.Replace | UpdateTypes.Delete)]
    [InlineData(UpdateTypes.Undefined)]
    public async Task WithoutTheOptIn_ADelete_DeliversNoEvent_WhateverTheUpdateTypesSay(UpdateTypes updateTypes)
    {
        var ct = TestContext.Current.CancellationToken;
        var withPreImage = await InsertAsync<RtWatchTarget>();
        var withoutPreImage = await InsertAsync<RtWatchTargetNoPreImage>();
        using var preImageStream = await Repository.WatchRtEntitiesAsync<RtWatchTarget>(
            new WatchStreamFilter { UpdateTypes = updateTypes }, ct);
        using var keyStream = await Repository.WatchRtEntitiesAsync<RtWatchTargetNoPreImage>(
            new WatchStreamFilter { UpdateTypes = updateTypes }, ct);
        await Task.Delay(CursorWarmup, ct);
        var firstDelete0 = FirstDeleteAsync(preImageStream, EventWaitTimeout);
        var firstDelete1 = FirstDeleteAsync(keyStream, EventWaitTimeout);

        await DeleteAsync(withPreImage);
        await DeleteAsync(withoutPreImage);

        (await firstDelete0).Should().BeNull();
        (await firstDelete1).Should().BeNull();
    }

    // ---- document key: a watch on the whole collection -------------------------------------------------------

    [Fact]
    public async Task OptIn_OnACollectionRootWithoutPreImages_DeliversTheDeleteWithItsRtId()
    {
        var ct = TestContext.Current.CancellationToken;
        var entity = await InsertAsync<RtWatchTargetNoPreImage>(e => e.Name = "to delete");
        using var stream = await Repository.WatchRtEntitiesAsync<RtWatchTargetNoPreImage>(
            new WatchStreamFilter { UpdateTypes = UpdateTypes.Delete, IncludeDeleteEvents = true }, ct);
        await Task.Delay(CursorWarmup, ct);
        var firstDelete0 = FirstDeleteAsync(stream, EventWaitTimeout);

        await DeleteAsync(entity);

        var evt = await firstDelete0;
        evt.Should().NotBeNull();
        evt!.DocumentRtId.Should().Be(entity.RtId);
        evt.Document.Should().BeNull();
    }

    [Fact]
    public async Task OptIn_WithAWatchedRtId_DeliversOnlyTheDeleteOfThatEntity()
    {
        var ct = TestContext.Current.CancellationToken;
        var watched = await InsertAsync<RtWatchTargetNoPreImage>();
        var other = await InsertAsync<RtWatchTargetNoPreImage>();
        using var stream = await Repository.WatchRtEntitiesAsync<RtWatchTargetNoPreImage>(
            new WatchStreamFilter
            {
                UpdateTypes = UpdateTypes.Delete, IncludeDeleteEvents = true, RtId = watched.RtId
            }, ct);
        await Task.Delay(CursorWarmup, ct);
        // Subscribed before the changes: the stream replays only its latest event to a late subscriber.
        var collecting = CollectAsync(stream, TimeSpan.FromSeconds(3));

        await DeleteAsync(other);
        await DeleteAsync(watched);

        var events = await collecting;
        events.Select(e => e.DocumentRtId).Should().Equal(watched.RtId);
    }

    [Fact]
    public async Task OptIn_KeepsTheOtherEventsAsBefore_AndTheyCarryTheirRtIdToo()
    {
        var ct = TestContext.Current.CancellationToken;
        using var stream = await Repository.WatchRtEntitiesAsync<RtWatchTargetNoPreImage>(
            new WatchStreamFilter
            {
                UpdateTypes = UpdateTypes.Insert | UpdateTypes.Delete, IncludeDeleteEvents = true
            }, ct);
        await Task.Delay(CursorWarmup, ct);
        // Subscribed before the changes: the stream replays only its latest event to a late subscriber.
        var collecting = CollectAsync(stream, TimeSpan.FromSeconds(3));

        var entity = await InsertAsync<RtWatchTargetNoPreImage>(e => e.Name = "new");
        await DeleteAsync(entity);

        var events = (await collecting)
            .Where(e => e.DocumentRtId == entity.RtId).ToList();
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
        using var stream = await Repository.WatchRtEntitiesAsync<RtWatchTargetDerived>(
            new WatchStreamFilter { UpdateTypes = UpdateTypes.Delete, IncludeDeleteEvents = true }, ct);
        await Task.Delay(CursorWarmup, ct);
        // Subscribed before the changes: the stream replays only its latest event to a late subscriber.
        var collecting = CollectAsync(stream, TimeSpan.FromSeconds(3));

        await DeleteAsync(baseEntity);
        await DeleteAsync(derived);

        var events = await collecting;
        var evt = events.Should().ContainSingle().Which;
        evt.DocumentRtId.Should().Be(derived.RtId);
        evt.DocumentBeforeChange!.RtId.Should().Be(derived.RtId);
    }

    [Fact]
    public async Task OptIn_WithPreImages_AppliesTheFieldFilterToTheDeletedDocument()
    {
        var ct = TestContext.Current.CancellationToken;
        var active = await InsertAsync<RtWatchTarget>(e => e.Status = "active");
        var inactive = await InsertAsync<RtWatchTarget>(e => e.Status = "inactive");
        using var stream = await Repository.WatchRtEntitiesAsync<RtWatchTarget>(
            new WatchStreamFilter
            {
                UpdateTypes = UpdateTypes.Delete,
                IncludeDeleteEvents = true,
                FieldFilterCriteria = FieldFilterCriteria.Create()
                    .Field(nameof(RtWatchTarget.Status), FieldFilterOperator.Equals, "active")
            }, ct);
        await Task.Delay(CursorWarmup, ct);
        // Subscribed before the changes: the stream replays only its latest event to a late subscriber.
        var collecting = CollectAsync(stream, TimeSpan.FromSeconds(3));

        await DeleteAsync(inactive);
        await DeleteAsync(active);

        var events = await collecting;
        events.Select(e => e.DocumentRtId).Should().Equal(active.RtId);
    }
}
