using Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantLifecycle;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.TenantLifecycle;
using MongoDB.Bson.Serialization;
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

    /// <summary>
    /// AB#4829. A setup pass that slipped past the Deleting gate (its lifecycle read predates the
    /// delete's MarkDeleting) used to flip the delete's tombstone back to Creating via EnsureCreating's
    /// new-cycle branch — corroding the very marker the settle sweep needs to finish the delete, and
    /// resurrecting the tenant for the reconciler. A Deleting record must survive EnsureCreating
    /// untouched, including its metadata (the sweep re-drops by the stored database name).
    /// </summary>
    [Fact]
    public async Task EnsureCreating_does_not_resurrect_a_Deleting_tombstone()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store;
        var tenantId = $"ld-{Guid.NewGuid():N}"[..20];

        var correlationId = Guid.NewGuid();
        await store.EnsureCreatingAsync(tenantId, $"db-{tenantId}", correlationId, ct);
        await store.MarkDeletingAsync(tenantId, ct);
        var tombstone = await store.GetAsync(tenantId, ct);

        await store.EnsureCreatingAsync(tenantId, "some-other-db", Guid.NewGuid(), ct);

        var record = await store.GetAsync(tenantId, ct);
        Assert.NotNull(record);
        Assert.Equal(TenantLifecycleState.Deleting, record!.State);
        Assert.Equal($"db-{tenantId}", record.DatabaseName);
        Assert.Equal(correlationId, record.CorrelationId);
        // The settle clock is the most load-bearing preserved field: if EnsureCreating restamped
        // LastTransitionUtc, a stream of PosUpdateTenant echoes would keep the tombstone younger than
        // the settle period forever and the sweep would never complete the delete.
        Assert.Equal(tombstone!.LastTransitionUtc, record.LastTransitionUtc);

        await store.RemoveAsync(tenantId, ct);
    }

    /// <summary>
    /// AB#4829. The delete writes its settle tombstone AFTER the database drop, via EnsureDeleting.
    /// MarkDeleting stays update-only (phantom-tombstone guard for arbitrary callers), but the delete
    /// endpoint has already proven the tenant exists — and a legacy tenant without a lifecycle record
    /// would otherwise end its delete without the anchor the settle sweep needs. The upsert also
    /// carries the database name so the sweep can re-drop a resurrected shell.
    /// </summary>
    [Fact]
    public async Task EnsureDeleting_upserts_the_settle_tombstone_even_for_a_legacy_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store;
        var tenantId = $"le-{Guid.NewGuid():N}"[..20];

        // Legacy tenant: no lifecycle record at all.
        Assert.Null(await store.GetAsync(tenantId, ct));

        var correlationId = Guid.NewGuid();
        await store.EnsureDeletingAsync(tenantId, $"db-{tenantId}", correlationId, ct);

        var inserted = await store.GetAsync(tenantId, ct);
        Assert.NotNull(inserted);
        Assert.Equal(TenantLifecycleState.Deleting, inserted!.State);
        Assert.Equal($"db-{tenantId}", inserted.DatabaseName);
        Assert.Equal(correlationId, inserted.CorrelationId);
        Assert.Null(inserted.LeaseOwner);

        // On an existing NON-Deleting record it transitions to Deleting and refreshes the stored
        // database name (start from a genuine Active record, not the tombstone from the first phase).
        await store.RemoveAsync(tenantId, ct);
        await store.EnsureCreatingAsync(tenantId, null, Guid.Empty, ct);
        await store.MarkActiveAsync(tenantId, cancellationToken: ct);
        await store.EnsureDeletingAsync(tenantId, $"db2-{tenantId}", correlationId, ct);
        var updated = await store.GetAsync(tenantId, ct);
        Assert.Equal(TenantLifecycleState.Deleting, updated!.State);
        Assert.Equal($"db2-{tenantId}", updated.DatabaseName);

        await store.RemoveAsync(tenantId, ct);
    }

    /// <summary>
    /// AB#4829 review follow-up. The Deleting-preservation invariant must hold for EVERY writer, not
    /// just EnsureCreating: MarkActive is the terminal write of exactly the in-flight setup pass the
    /// invariant defends against — a pass that entered before the delete's tombstone and completes
    /// after it would flip the tombstone to Active, and the settle sweep (which only processes
    /// Deleting records) would never finish the delete; a resurrected shell database would then
    /// permanently block its own name. Same for MarkFailed via a late reconciler give-up, and for
    /// RequeueForReconcileAsync via the operator rerunSetup endpoint, which the lifecycle GET showing
    /// "Deleting" practically invites during the settle window. Only EnsureDeleting/Remove may leave
    /// the Deleting state.
    /// </summary>
    [Fact]
    public async Task No_writer_overwrites_a_Deleting_tombstone()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store;
        var tenantId = $"lw-{Guid.NewGuid():N}"[..20];

        await store.EnsureCreatingAsync(tenantId, $"db-{tenantId}", Guid.NewGuid(), ct);
        await store.MarkDeletingAsync(tenantId, ct);
        var tombstone = await store.GetAsync(tenantId, ct);

        await store.MarkActiveAsync(tenantId, "hijacked-db", ct);
        var afterActive = await store.GetAsync(tenantId, ct);
        Assert.Equal(TenantLifecycleState.Deleting, afterActive!.State);
        Assert.Equal($"db-{tenantId}", afterActive.DatabaseName);
        Assert.Equal(tombstone!.LastTransitionUtc, afterActive.LastTransitionUtc);

        await store.MarkFailedAsync(tenantId, "late reconciler give-up", ct);
        var afterFailed = await store.GetAsync(tenantId, ct);
        Assert.Equal(TenantLifecycleState.Deleting, afterFailed!.State);
        Assert.Null(afterFailed.LastError);

        Assert.Null(await store.RequeueForReconcileAsync(tenantId, ct));
        Assert.Equal(TenantLifecycleState.Deleting, (await store.GetAsync(tenantId, ct))!.State);

        await store.RemoveAsync(tenantId, ct);
    }

    [Fact]
    public async Task MarkActive_still_backfills_a_missing_record_and_activates_a_Creating_one()
    {
        // The Deleting guard must not break MarkActive's two legitimate jobs: lazy backfill (a legacy
        // tenant without a record reaches MarkActive on startup) and the normal Creating -> Active
        // transition.
        var ct = TestContext.Current.CancellationToken;
        var store = Store;
        var tenantId = $"lb-{Guid.NewGuid():N}"[..20];

        await store.MarkActiveAsync(tenantId, $"db-{tenantId}", ct);
        var backfilled = await store.GetAsync(tenantId, ct);
        Assert.Equal(TenantLifecycleState.Active, backfilled!.State);
        Assert.Equal($"db-{tenantId}", backfilled.DatabaseName);
        await store.RemoveAsync(tenantId, ct);

        await store.EnsureCreatingAsync(tenantId, null, Guid.Empty, ct);
        await store.MarkActiveAsync(tenantId, cancellationToken: ct);
        Assert.Equal(TenantLifecycleState.Active, (await store.GetAsync(tenantId, ct))!.State);

        await store.RemoveAsync(tenantId, ct);
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

    /// <summary>
    /// AB#4690 regression. A tenant whose setup keeps being re-run (PosCreateTenant / PosUpdateTenant
    /// arrive continuously in a live cluster) must not have the reconciler's bookkeeping wiped by
    /// <see cref="ITenantLifecycleStore.EnsureCreatingAsync"/>. Before the fix this reset the lease and
    /// reverted the attempt increment, so a stalled tenant stayed at AttemptCount 0 forever, never
    /// exhausted its retry budget and never reached Failed with a diagnosable LastError.
    /// </summary>
    [Fact]
    public async Task EnsureCreating_keeps_the_reconcile_lease_and_attempt_count_of_a_creating_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store;
        var tenantId = $"lk-{Guid.NewGuid():N}"[..20];

        await store.EnsureCreatingAsync(tenantId, $"db-{tenantId}", Guid.NewGuid(), ct);

        var claimed = await ClaimAsync(store, tenantId, "owner-a", ct);
        Assert.Equal(1, claimed.AttemptCount);
        Assert.Equal("owner-a", claimed.LeaseOwner);

        // The setup run that races the reconciler.
        await store.EnsureCreatingAsync(tenantId, $"db-{tenantId}", Guid.Empty, ct);

        var afterSetup = await store.GetAsync(tenantId, ct);
        Assert.NotNull(afterSetup);
        Assert.Equal(TenantLifecycleState.Creating, afterSetup!.State);
        Assert.Equal(1, afterSetup.AttemptCount);
        Assert.Equal("owner-a", afterSetup.LeaseOwner);
        Assert.Equal(claimed.LeaseUntil, afterSetup.LeaseUntil);
        Assert.Equal(claimed.CreatedUtc, afterSetup.CreatedUtc);

        // ... and the tenant is still single-flight: the live lease keeps a second reconciler out.
        Assert.Null(await store.TryClaimForReconcileAsync("owner-b", TimeSpan.FromMinutes(5), ct));

        await store.RemoveAsync(tenantId, ct);
    }

    /// <summary>
    /// The counterpart of the test above: a record that is NOT already Creating (a stale Failed or
    /// Deleting tombstone from a previous incarnation of the tenant) starts a genuinely new creation
    /// cycle, so the attempt budget, the lease and the last error are reset.
    /// </summary>
    [Fact]
    public async Task EnsureCreating_resets_the_attempt_budget_for_a_new_creation_cycle()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store;
        var tenantId = $"ln-{Guid.NewGuid():N}"[..20];

        await store.EnsureCreatingAsync(tenantId, $"db-{tenantId}", Guid.NewGuid(), ct);
        await store.MarkFailedAsync(tenantId, "boom", ct);
        Assert.Equal(1, (await store.GetAsync(tenantId, ct))!.AttemptCount);

        await store.EnsureCreatingAsync(tenantId, null, Guid.Empty, ct);

        var restarted = await store.GetAsync(tenantId, ct);
        Assert.Equal(TenantLifecycleState.Creating, restarted!.State);
        Assert.Equal(TenantLifecyclePhase.SetupStarted, restarted.Phase);
        Assert.Equal(0, restarted.AttemptCount);
        Assert.Null(restarted.LastError);
        Assert.Null(restarted.LeaseOwner);
        Assert.Null(restarted.LeaseUntil);
        // A null database name keeps the one already stored instead of erasing it.
        Assert.Equal($"db-{tenantId}", restarted.DatabaseName);

        await store.RemoveAsync(tenantId, ct);
    }

    /// <summary>
    /// <see cref="TenantLifecycleStore.EnsureCreatingAsync"/> is a hand-written aggregation-pipeline update
    /// (the typed builders cannot express <c>$cond</c>), so it addresses fields by their stored element
    /// name. A property rename or a change to the camelCase convention would make the pipeline write to
    /// fields nobody reads — silently, with no error. Pin the names against the class map.
    /// </summary>
    [Fact]
    public async Task Stored_element_names_match_the_class_map()
    {
        // Touch the store first so it has registered the class map through the production code path
        // (which is also what applies the engine's global camelCase convention).
        await Store.GetAsync("does-not-exist", TestContext.Current.CancellationToken);

        var classMap = BsonClassMap.LookupClassMap(typeof(TenantLifecycleRecord));
        string ElementNameOf(string propertyName) => classMap.GetMemberMap(propertyName).ElementName;

        Assert.Equal(TenantLifecycleStore.Fields.TenantId, ElementNameOf(nameof(TenantLifecycleRecord.TenantId)));
        Assert.Equal(TenantLifecycleStore.Fields.DatabaseName, ElementNameOf(nameof(TenantLifecycleRecord.DatabaseName)));
        Assert.Equal(TenantLifecycleStore.Fields.CorrelationId, ElementNameOf(nameof(TenantLifecycleRecord.CorrelationId)));
        Assert.Equal(TenantLifecycleStore.Fields.State, ElementNameOf(nameof(TenantLifecycleRecord.State)));
        Assert.Equal(TenantLifecycleStore.Fields.Phase, ElementNameOf(nameof(TenantLifecycleRecord.Phase)));
        Assert.Equal(TenantLifecycleStore.Fields.AttemptCount, ElementNameOf(nameof(TenantLifecycleRecord.AttemptCount)));
        Assert.Equal(TenantLifecycleStore.Fields.LastError, ElementNameOf(nameof(TenantLifecycleRecord.LastError)));
        Assert.Equal(TenantLifecycleStore.Fields.CreatedUtc, ElementNameOf(nameof(TenantLifecycleRecord.CreatedUtc)));
        Assert.Equal(TenantLifecycleStore.Fields.LastTransitionUtc, ElementNameOf(nameof(TenantLifecycleRecord.LastTransitionUtc)));
        Assert.Equal(TenantLifecycleStore.Fields.LeaseOwner, ElementNameOf(nameof(TenantLifecycleRecord.LeaseOwner)));
        Assert.Equal(TenantLifecycleStore.Fields.LeaseUntil, ElementNameOf(nameof(TenantLifecycleRecord.LeaseUntil)));
    }

    /// <summary>
    /// Claims until the expected tenant is the one handed out. The store deliberately claims the
    /// longest-waiting Creating tenant of the whole system database, so a leftover record from an
    /// earlier run must not make the test flaky. Foreign claims are released again immediately.
    /// </summary>
    private static async Task<TenantLifecycleRecord> ClaimAsync(ITenantLifecycleStore store, string tenantId,
        string owner, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var claimed = await store.TryClaimForReconcileAsync(owner, TimeSpan.FromMinutes(5), ct);
            Assert.NotNull(claimed);
            if (claimed!.TenantId == tenantId)
            {
                return claimed;
            }

            await store.ReleaseLeaseAsync(claimed.TenantId, owner, ct);
        }

        throw new InvalidOperationException($"Tenant '{tenantId}' was never handed out for reconcile.");
    }

    [Fact]
    public async Task Requeue_reopens_a_failed_tenant_for_reconcile()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store;
        var tenantId = $"lr-{Guid.NewGuid():N}"[..20];

        // Requeue on a tenant with no record returns null (nothing to reopen).
        Assert.Null(await store.RequeueForReconcileAsync(tenantId, ct));

        await store.EnsureCreatingAsync(tenantId, null, Guid.NewGuid(), ct);
        await store.MarkFailedAsync(tenantId, "gave up", ct);
        Assert.Equal(TenantLifecycleState.Failed, (await store.GetAsync(tenantId, ct))!.State);

        // Requeue re-opens it: Creating, attempt budget reset, error/lease cleared.
        var reopened = await store.RequeueForReconcileAsync(tenantId, ct);
        Assert.NotNull(reopened);
        Assert.Equal(TenantLifecycleState.Creating, reopened!.State);
        Assert.Equal(0, reopened.AttemptCount);
        Assert.Null(reopened.LastError);
        Assert.Null(reopened.LeaseOwner);

        // A reopened tenant is claimable by the reconciler again.
        var claim = await store.TryClaimForReconcileAsync("owner", TimeSpan.FromMinutes(5), ct);
        Assert.NotNull(claim);
        Assert.Equal(tenantId, claim!.TenantId);

        await store.RemoveAsync(tenantId, ct);
    }
}
