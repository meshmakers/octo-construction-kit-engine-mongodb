using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;

using FluentAssertions;

using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

using TestCkModel.Generated.Test.v1;

using Xunit;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

[Collection(ImportTestCkModelCollection.Name)]
public class WatchRtEntitiesFilterTests(ImportTestCkModelFixture fixture)
{
    // Give the change-stream cursor time to open before mutating.
    private static readonly TimeSpan CursorWarmup = TimeSpan.FromMilliseconds(500);

    // Event arrival budget once the cursor is open and the mutation is committed.
    private static readonly TimeSpan EventWaitTimeout = TimeSpan.FromSeconds(5);

    // Negative window: how long to watch for an event that must NOT arrive.
    private static readonly TimeSpan NoEventWindow = TimeSpan.FromSeconds(1);

    private static async Task<IUpdateInfo<T>?> ReceiveOneAsync<T>(
        IUpdateStream<T> stream, TimeSpan timeout)
    {
        try
        {
            return await stream.GetUpdates().FirstAsync().Timeout(timeout).ToTask();
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    [Fact]
    public async Task Watch_WithNoFilters_FiresOnUpdate()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantRepository = fixture.GetSystemContext().GetTenantRepository();

        // Seed: insert an entity before subscribing so the insert event is not part of the watch window.
        var entity = await tenantRepository.CreateTransientRtEntityAsync<RtWatchTarget>();
        entity.Name = "alpha";
        using var seedSession = await tenantRepository.GetSessionAsync();
        seedSession.StartTransaction();
        await tenantRepository.InsertOneRtEntityAsync(seedSession, entity);
        await seedSession.CommitTransactionAsync();

        // Open subscription after the seed insert.
        using var stream = await tenantRepository.WatchRtEntitiesAsync<RtWatchTarget>(
            new WatchStreamFilter { UpdateTypes = UpdateTypes.Update }, ct);

        // Allow the background change-stream cursor to open before mutating.
        await Task.Delay(CursorWarmup, ct);

        // Mutate after the subscription is open.
        entity.Name = "beta";
        using var updateSession = await tenantRepository.GetSessionAsync();
        updateSession.StartTransaction();
        await tenantRepository.UpdateOneRtEntityByIdAsync(updateSession, entity.RtId, entity);
        await updateSession.CommitTransactionAsync();

        var evt = await ReceiveOneAsync(stream, EventWaitTimeout);
        evt.Should().NotBeNull("an unfiltered watch must fire on every update");
    }

    [Fact]
    public async Task Watch_WithAfterFilter_FiresWhenAfterMatches()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantRepository = fixture.GetSystemContext().GetTenantRepository();

        // Seed entity before subscribing.
        var entity = await tenantRepository.CreateTransientRtEntityAsync<RtWatchTarget>();
        entity.Name = "alpha";
        using var seedSession = await tenantRepository.GetSessionAsync();
        seedSession.StartTransaction();
        await tenantRepository.InsertOneRtEntityAsync(seedSession, entity);
        await seedSession.CommitTransactionAsync();

        // Filter: only fire when post-image Status == "active".
        var afterFilter = FieldFilterCriteria.Create()
            .Field(nameof(RtWatchTarget.Status), FieldFilterOperator.Equals, "active");

        using var stream = await tenantRepository.WatchRtEntitiesAsync<RtWatchTarget>(
            new WatchStreamFilter
            {
                UpdateTypes = UpdateTypes.Update,
                FieldFilterCriteria = afterFilter,
            }, ct);

        await Task.Delay(CursorWarmup, ct);

        // Update sets Status to "active" — should match the after-filter.
        entity.Status = "active";
        using var updateSession = await tenantRepository.GetSessionAsync();
        updateSession.StartTransaction();
        await tenantRepository.UpdateOneRtEntityByIdAsync(updateSession, entity.RtId, entity);
        await updateSession.CommitTransactionAsync();

        var evt = await ReceiveOneAsync(stream, EventWaitTimeout);
        evt.Should().NotBeNull("update sets Status=active which matches the after-filter");
    }

    [Fact]
    public async Task Watch_WithAfterFilter_DoesNotFireWhenAfterDoesNotMatch()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantRepository = fixture.GetSystemContext().GetTenantRepository();

        // Seed entity before subscribing.
        var entity = await tenantRepository.CreateTransientRtEntityAsync<RtWatchTarget>();
        entity.Name = "alpha";
        using var seedSession = await tenantRepository.GetSessionAsync();
        seedSession.StartTransaction();
        await tenantRepository.InsertOneRtEntityAsync(seedSession, entity);
        await seedSession.CommitTransactionAsync();

        // Filter: only fire when post-image Status == "active".
        var afterFilter = FieldFilterCriteria.Create()
            .Field(nameof(RtWatchTarget.Status), FieldFilterOperator.Equals, "active");

        using var stream = await tenantRepository.WatchRtEntitiesAsync<RtWatchTarget>(
            new WatchStreamFilter
            {
                UpdateTypes = UpdateTypes.Update,
                FieldFilterCriteria = afterFilter,
            }, ct);

        await Task.Delay(CursorWarmup, ct);

        // Update sets Status to "inactive" — must NOT match the after-filter.
        entity.Status = "inactive";
        using var updateSession = await tenantRepository.GetSessionAsync();
        updateSession.StartTransaction();
        await tenantRepository.UpdateOneRtEntityByIdAsync(updateSession, entity.RtId, entity);
        await updateSession.CommitTransactionAsync();

        var evt = await ReceiveOneAsync(stream, NoEventWindow);
        evt.Should().BeNull("after-filter must suppress events whose post-image does not match");
    }

    [Fact]
    public async Task Watch_WithBeforeFilter_FiresWhenBeforeMatches()
    {
        // Regression for Bug 1: before this fix, BuildExtensions.Inject hardcoded the
        // 'fullDocument.' prefix on the before-filter, causing it to evaluate against the
        // post-image instead of the pre-image. The test proves the before-filter now
        // correctly targets fullDocumentBeforeChange.
        var ct = TestContext.Current.CancellationToken;
        var tenantRepository = fixture.GetSystemContext().GetTenantRepository();

        // Seed: Status = "pending". Insert so the pre-image carries that value.
        var entity = await tenantRepository.CreateTransientRtEntityAsync<RtWatchTarget>();
        entity.Status = "pending";
        using var seedSession = await tenantRepository.GetSessionAsync();
        seedSession.StartTransaction();
        await tenantRepository.InsertOneRtEntityAsync(seedSession, entity);
        await seedSession.CommitTransactionAsync();

        // Before-filter: pre-image Status must equal "pending".
        var beforeFilter = FieldFilterCriteria.Create()
            .Field(nameof(RtWatchTarget.Status), FieldFilterOperator.Equals, "pending");

        using var stream = await tenantRepository.WatchRtEntitiesAsync<RtWatchTarget>(
            new WatchStreamFilter
            {
                UpdateTypes = UpdateTypes.Update,
                BeforeFieldFilterCriteria = beforeFilter,
            }, ct);

        await Task.Delay(CursorWarmup, ct);

        // Mutate: transition pending -> active. Pre-image is "pending" (matches filter);
        // post-image is "active" (irrelevant to the before-filter).
        entity.Status = "active";
        using var updateSession = await tenantRepository.GetSessionAsync();
        updateSession.StartTransaction();
        await tenantRepository.UpdateOneRtEntityByIdAsync(updateSession, entity.RtId, entity);
        await updateSession.CommitTransactionAsync();

        var evt = await ReceiveOneAsync(stream, EventWaitTimeout);
        evt.Should().NotBeNull(
            "pre-image Status=pending matches the before-filter regardless of post-image value");
    }

    [Fact]
    public async Task Watch_WithBeforeFilter_DoesNotFireWhenBeforeDoesNotMatch()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantRepository = fixture.GetSystemContext().GetTenantRepository();

        // Seed: Status = "active" — does NOT match the before-filter value ("pending").
        var entity = await tenantRepository.CreateTransientRtEntityAsync<RtWatchTarget>();
        entity.Status = "active";
        using var seedSession = await tenantRepository.GetSessionAsync();
        seedSession.StartTransaction();
        await tenantRepository.InsertOneRtEntityAsync(seedSession, entity);
        await seedSession.CommitTransactionAsync();

        var beforeFilter = FieldFilterCriteria.Create()
            .Field(nameof(RtWatchTarget.Status), FieldFilterOperator.Equals, "pending");

        using var stream = await tenantRepository.WatchRtEntitiesAsync<RtWatchTarget>(
            new WatchStreamFilter
            {
                UpdateTypes = UpdateTypes.Update,
                BeforeFieldFilterCriteria = beforeFilter,
            }, ct);

        await Task.Delay(CursorWarmup, ct);

        // Mutate: any update. Pre-image is "active" which does NOT match; event must be suppressed.
        entity.Status = "done";
        using var updateSession = await tenantRepository.GetSessionAsync();
        updateSession.StartTransaction();
        await tenantRepository.UpdateOneRtEntityByIdAsync(updateSession, entity.RtId, entity);
        await updateSession.CommitTransactionAsync();

        var evt = await ReceiveOneAsync(stream, NoEventWindow);
        evt.Should().BeNull(
            "pre-image Status=active does not match before-filter Status=pending");
    }

    [Fact]
    public async Task Watch_WithBeforeAndAfterFilters_FiresWhenBothMatch()
    {
        // Regression for Bug 2: before the fix, the two filters were OR'd so this
        // test would also pass by accident when only one side matched. With the And
        // composition, both filters must hold for the event to fire — and this test
        // constructs the 'both match' scenario.
        var ct = TestContext.Current.CancellationToken;
        var tenantRepository = fixture.GetSystemContext().GetTenantRepository();

        // Seed: Status = "pending" (pre-image matches before-filter).
        var entity = await tenantRepository.CreateTransientRtEntityAsync<RtWatchTarget>();
        entity.Status = "pending";
        using var seedSession = await tenantRepository.GetSessionAsync();
        seedSession.StartTransaction();
        await tenantRepository.InsertOneRtEntityAsync(seedSession, entity);
        await seedSession.CommitTransactionAsync();

        var beforeFilter = FieldFilterCriteria.Create()
            .Field(nameof(RtWatchTarget.Status), FieldFilterOperator.Equals, "pending");
        var afterFilter = FieldFilterCriteria.Create()
            .Field(nameof(RtWatchTarget.Status), FieldFilterOperator.Equals, "active");

        using var stream = await tenantRepository.WatchRtEntitiesAsync<RtWatchTarget>(
            new WatchStreamFilter
            {
                UpdateTypes = UpdateTypes.Update,
                BeforeFieldFilterCriteria = beforeFilter,
                FieldFilterCriteria = afterFilter,
            }, ct);

        await Task.Delay(CursorWarmup, ct);

        // Mutate: pending -> active. Pre-image matches before-filter, post-image matches after-filter.
        entity.Status = "active";
        using var updateSession = await tenantRepository.GetSessionAsync();
        updateSession.StartTransaction();
        await tenantRepository.UpdateOneRtEntityByIdAsync(updateSession, entity.RtId, entity);
        await updateSession.CommitTransactionAsync();

        var evt = await ReceiveOneAsync(stream, EventWaitTimeout);
        evt.Should().NotBeNull("both pre- and post-image filters match — event must fire");
    }

    [Fact]
    public async Task Watch_WithBeforeAndAfterFilters_DoesNotFireWhenOnlyAfterMatches()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantRepository = fixture.GetSystemContext().GetTenantRepository();

        // Seed: Status = "done" — pre-image does NOT match the before-filter ("pending").
        var entity = await tenantRepository.CreateTransientRtEntityAsync<RtWatchTarget>();
        entity.Status = "done";
        using var seedSession = await tenantRepository.GetSessionAsync();
        seedSession.StartTransaction();
        await tenantRepository.InsertOneRtEntityAsync(seedSession, entity);
        await seedSession.CommitTransactionAsync();

        var beforeFilter = FieldFilterCriteria.Create()
            .Field(nameof(RtWatchTarget.Status), FieldFilterOperator.Equals, "pending");
        var afterFilter = FieldFilterCriteria.Create()
            .Field(nameof(RtWatchTarget.Status), FieldFilterOperator.Equals, "active");

        using var stream = await tenantRepository.WatchRtEntitiesAsync<RtWatchTarget>(
            new WatchStreamFilter
            {
                UpdateTypes = UpdateTypes.Update,
                BeforeFieldFilterCriteria = beforeFilter,
                FieldFilterCriteria = afterFilter,
            }, ct);

        await Task.Delay(CursorWarmup, ct);

        // Mutate: done -> active. Post-image matches after-filter, but pre-image is "done", not "pending".
        entity.Status = "active";
        using var updateSession = await tenantRepository.GetSessionAsync();
        updateSession.StartTransaction();
        await tenantRepository.UpdateOneRtEntityByIdAsync(updateSession, entity.RtId, entity);
        await updateSession.CommitTransactionAsync();

        var evt = await ReceiveOneAsync(stream, NoEventWindow);
        evt.Should().BeNull(
            "only post-image matches; And composition must suppress the event");
    }

    [Fact]
    public async Task Watch_WithBeforeAndAfterFilters_DoesNotFireWhenOnlyBeforeMatches()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantRepository = fixture.GetSystemContext().GetTenantRepository();

        // Seed: Status = "pending" — pre-image matches the before-filter.
        var entity = await tenantRepository.CreateTransientRtEntityAsync<RtWatchTarget>();
        entity.Status = "pending";
        using var seedSession = await tenantRepository.GetSessionAsync();
        seedSession.StartTransaction();
        await tenantRepository.InsertOneRtEntityAsync(seedSession, entity);
        await seedSession.CommitTransactionAsync();

        var beforeFilter = FieldFilterCriteria.Create()
            .Field(nameof(RtWatchTarget.Status), FieldFilterOperator.Equals, "pending");
        var afterFilter = FieldFilterCriteria.Create()
            .Field(nameof(RtWatchTarget.Status), FieldFilterOperator.Equals, "active");

        using var stream = await tenantRepository.WatchRtEntitiesAsync<RtWatchTarget>(
            new WatchStreamFilter
            {
                UpdateTypes = UpdateTypes.Update,
                BeforeFieldFilterCriteria = beforeFilter,
                FieldFilterCriteria = afterFilter,
            }, ct);

        await Task.Delay(CursorWarmup, ct);

        // Mutate: pending -> done. Pre-image matches before-filter, but post-image is "done", not "active".
        entity.Status = "done";
        using var updateSession = await tenantRepository.GetSessionAsync();
        updateSession.StartTransaction();
        await tenantRepository.UpdateOneRtEntityByIdAsync(updateSession, entity.RtId, entity);
        await updateSession.CommitTransactionAsync();

        var evt = await ReceiveOneAsync(stream, NoEventWindow);
        evt.Should().BeNull(
            "only pre-image matches; And composition must suppress the event");
    }

    [Fact]
    public async Task Watch_ThrowsWhenBeforeFieldFilterCriteriaSetAndPreImageCaptureDisabled()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenantRepository = fixture.GetSystemContext().GetTenantRepository();

        var beforeFilter = FieldFilterCriteria.Create()
            .Field(nameof(RtWatchTargetNoPreImage.Name), FieldFilterOperator.Equals, "anything");

        var act = async () => await tenantRepository.WatchRtEntitiesAsync<RtWatchTargetNoPreImage>(
            new WatchStreamFilter
            {
                UpdateTypes = UpdateTypes.Update,
                BeforeFieldFilterCriteria = beforeFilter,
            }, ct);

        var assertion = await act.Should().ThrowAsync<OperationFailedException>(
            "WatchTargetNoPreImage is a collection root WITHOUT enableChangeStreamPreAndPostImages; the guard must fail fast");
        assertion.Which.Message.Should()
            .Contain("WatchTargetNoPreImage")
            .And.Contain("EnableChangeStreamPreAndPostImages");
    }

    [Fact]
    public async Task Watch_WithOnlyAfterFieldFilter_WorksOnTypeWithoutPreImageCapture()
    {
        // The guard must only fire when BeforeFieldFilterCriteria is set.
        // With only an after-filter, the subscription must be accepted and deliver events.
        var ct = TestContext.Current.CancellationToken;
        var tenantRepository = fixture.GetSystemContext().GetTenantRepository();

        var entity = await tenantRepository.CreateTransientRtEntityAsync<RtWatchTargetNoPreImage>();
        entity.Name = "alpha";
        using var seedSession = await tenantRepository.GetSessionAsync();
        seedSession.StartTransaction();
        await tenantRepository.InsertOneRtEntityAsync(seedSession, entity);
        await seedSession.CommitTransactionAsync();

        var afterFilter = FieldFilterCriteria.Create()
            .Field(nameof(RtWatchTargetNoPreImage.Name), FieldFilterOperator.Equals, "beta");

        using var stream = await tenantRepository.WatchRtEntitiesAsync<RtWatchTargetNoPreImage>(
            new WatchStreamFilter
            {
                UpdateTypes = UpdateTypes.Update,
                FieldFilterCriteria = afterFilter,
            }, ct);

        await Task.Delay(CursorWarmup, ct);

        entity.Name = "beta";
        using var updateSession = await tenantRepository.GetSessionAsync();
        updateSession.StartTransaction();
        await tenantRepository.UpdateOneRtEntityByIdAsync(updateSession, entity.RtId, entity);
        await updateSession.CommitTransactionAsync();

        var evt = await ReceiveOneAsync(stream, EventWaitTimeout);
        evt.Should().NotBeNull(
            "guard must not fire when only an after-filter is configured; event must be delivered");
    }

    [Fact]
    public async Task Watch_OnDerivedType_UsesCollectionRootFlagForGuard()
    {
        // WatchTargetDerived derives from WatchTarget (root HAS the flag). The guard walks to the
        // defining collection root, reads its EnableChangeStreamPreAndPostImages flag, and accepts
        // the subscription — then the event fires on update.
        var ct = TestContext.Current.CancellationToken;
        var tenantRepository = fixture.GetSystemContext().GetTenantRepository();

        var entity = await tenantRepository.CreateTransientRtEntityAsync<RtWatchTargetDerived>();
        entity.Status = "pending";
        using var seedSession = await tenantRepository.GetSessionAsync();
        seedSession.StartTransaction();
        await tenantRepository.InsertOneRtEntityAsync(seedSession, entity);
        await seedSession.CommitTransactionAsync();

        var beforeFilter = FieldFilterCriteria.Create()
            .Field(nameof(RtWatchTargetDerived.Status), FieldFilterOperator.Equals, "pending");

        using var stream = await tenantRepository.WatchRtEntitiesAsync<RtWatchTargetDerived>(
            new WatchStreamFilter
            {
                UpdateTypes = UpdateTypes.Update,
                BeforeFieldFilterCriteria = beforeFilter,
            }, ct);

        await Task.Delay(CursorWarmup, ct);

        entity.Status = "active";
        using var updateSession = await tenantRepository.GetSessionAsync();
        updateSession.StartTransaction();
        await tenantRepository.UpdateOneRtEntityByIdAsync(updateSession, entity.RtId, entity);
        await updateSession.CommitTransactionAsync();

        var evt = await ReceiveOneAsync(stream, EventWaitTimeout);
        evt.Should().NotBeNull(
            "guard walked to the defining collection root WatchTarget which has the flag set — event must fire");
    }
}
