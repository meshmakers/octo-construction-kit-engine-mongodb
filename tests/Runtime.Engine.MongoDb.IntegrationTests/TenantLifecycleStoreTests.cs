using Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantLifecycle;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
/// Integration tests for the durable tenant-lifecycle store (AB#4348). Exercises the state machine and
/// the backfill / no-downgrade semantics against a real MongoDB system database.
/// </summary>
[Collection(SystemCollection.Name)]
public class TenantLifecycleStoreTests(SystemFixture fixture)
{
    private ITenantLifecycleStore Store => fixture.GetService<ITenantLifecycleStore>();

    [Fact]
    public async Task Transitions_persist_and_backfill_semantics_hold()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store;
        var tenantId = $"lc-{Guid.NewGuid():N}"[..20];

        // Missing until the first write.
        Assert.Null(await store.GetAsync(tenantId, ct));

        // EnsureCreating inserts a Creating record.
        var correlationId = Guid.NewGuid();
        await store.EnsureCreatingAsync(tenantId, $"db-{tenantId}", correlationId, ct);
        var creating = await store.GetAsync(tenantId, ct);
        Assert.NotNull(creating);
        Assert.Equal(TenantLifecycleState.Creating, creating!.State);
        Assert.Equal(TenantLifecyclePhase.SetupStarted, creating.Phase);
        Assert.Equal($"db-{tenantId}", creating.DatabaseName);
        Assert.Equal(correlationId, creating.CorrelationId);

        // Idempotent: a second EnsureCreating does not create a duplicate row.
        await store.EnsureCreatingAsync(tenantId, null, Guid.Empty, ct);
        Assert.Single(await store.ListAsync(ct), r => r.TenantId == tenantId);

        // MarkActive -> Active.
        await store.MarkActiveAsync(tenantId, cancellationToken: ct);
        Assert.Equal(TenantLifecycleState.Active, (await store.GetAsync(tenantId, ct))!.State);

        // EnsureCreating must NOT downgrade an already-Active tenant (this is the startup re-seed /
        // lazy-backfill path — a healthy tenant re-running setup stays Active).
        await store.EnsureCreatingAsync(tenantId, null, Guid.NewGuid(), ct);
        Assert.Equal(TenantLifecycleState.Active, (await store.GetAsync(tenantId, ct))!.State);

        // MarkFailed -> Failed, capturing the error and bumping the attempt count.
        await store.MarkFailedAsync(tenantId, "boom", ct);
        var failed = await store.GetAsync(tenantId, ct);
        Assert.Equal(TenantLifecycleState.Failed, failed!.State);
        Assert.Equal("boom", failed.LastError);
        Assert.Equal(1, failed.AttemptCount);

        // A fresh setup after a stale Failed/Deleting record resets it back to Creating (re-create).
        await store.EnsureCreatingAsync(tenantId, null, Guid.Empty, ct);
        Assert.Equal(TenantLifecycleState.Creating, (await store.GetAsync(tenantId, ct))!.State);

        // MarkDeleting -> Deleting tombstone; Remove -> gone.
        await store.MarkDeletingAsync(tenantId, ct);
        Assert.Equal(TenantLifecycleState.Deleting, (await store.GetAsync(tenantId, ct))!.State);

        await store.RemoveAsync(tenantId, ct);
        Assert.Null(await store.GetAsync(tenantId, ct));

        // MarkDeleting is update-only: it must NOT resurrect a record for a tenant that has none
        // (e.g. a legacy tenant), otherwise a re-create would be blocked by a phantom tombstone.
        await store.MarkDeletingAsync(tenantId, ct);
        Assert.Null(await store.GetAsync(tenantId, ct));
    }

    [Fact]
    public async Task Reconcile_lease_is_single_flight()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store;
        var tenantId = $"ll-{Guid.NewGuid():N}"[..20];

        await store.EnsureCreatingAsync(tenantId, null, Guid.NewGuid(), ct);

        // First claim leases the tenant and bumps the attempt count.
        var first = await store.TryClaimForReconcileAsync("owner-a", TimeSpan.FromMinutes(5), ct);
        Assert.NotNull(first);
        Assert.Equal(tenantId, first!.TenantId);
        Assert.Equal(1, first.AttemptCount);
        Assert.Equal("owner-a", first.LeaseOwner);

        // A second claim finds nothing — the only Creating tenant is leased and not yet expired.
        Assert.Null(await store.TryClaimForReconcileAsync("owner-b", TimeSpan.FromMinutes(5), ct));

        // Releasing the lease makes it claimable again (by a different owner), bumping the attempt again.
        await store.ReleaseLeaseAsync(tenantId, "owner-a", ct);
        var third = await store.TryClaimForReconcileAsync("owner-b", TimeSpan.FromMinutes(5), ct);
        Assert.NotNull(third);
        Assert.Equal(tenantId, third!.TenantId);
        Assert.Equal(2, third.AttemptCount);

        // Once Active, the lease is cleared and the tenant is no longer claimable.
        await store.MarkActiveAsync(tenantId, cancellationToken: ct);
        var active = await store.GetAsync(tenantId, ct);
        Assert.Equal(TenantLifecycleState.Active, active!.State);
        Assert.Null(active.LeaseOwner);
        Assert.Null(await store.TryClaimForReconcileAsync("owner-c", TimeSpan.FromMinutes(5), ct));

        await store.RemoveAsync(tenantId, ct);
    }
}
