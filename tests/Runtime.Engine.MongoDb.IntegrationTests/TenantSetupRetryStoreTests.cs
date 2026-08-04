using Meshmakers.Octo.Runtime.Contracts.MongoDb.TenantLifecycle;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Collections;
using Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests;

/// <summary>
/// Integration tests for the durable per-service tenant-setup retry queue (AB#4690).
/// </summary>
[Collection(SystemCollection.Name)]
public class TenantSetupRetryStoreTests(SystemFixture fixture)
{
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan NoWait = TimeSpan.Zero;
    private const int MaxAttempts = 10;

    private ITenantSetupRetryStore Store => fixture.GetService<ITenantSetupRetryStore>();

    [Fact]
    public async Task Failures_accumulate_and_are_cleared_on_success()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store;
        var serviceId = $"svc-{Guid.NewGuid():N}"[..16];
        var tenantId = $"sr-{Guid.NewGuid():N}"[..20];

        Assert.Empty(await store.ListAsync(serviceId, ct));

        await store.RecordFailureAsync(serviceId, tenantId, "boom", ct);
        var pending = Assert.Single(await store.ListAsync(serviceId, ct));
        Assert.Equal(tenantId, pending.TenantId);
        Assert.Equal(1, pending.AttemptCount);
        Assert.Equal("boom", pending.LastError);
        Assert.Null(pending.LeaseOwner);

        // A second failure updates the same row instead of adding one, and keeps the first-failure stamp.
        await store.RecordFailureAsync(serviceId, tenantId, "boom again", ct);
        var second = Assert.Single(await store.ListAsync(serviceId, ct));
        Assert.Equal(2, second.AttemptCount);
        Assert.Equal("boom again", second.LastError);
        Assert.Equal(pending.FirstFailureUtc, second.FirstFailureUtc);

        // Success removes the entry.
        await store.ClearAsync(serviceId, tenantId, ct);
        Assert.Empty(await store.ListAsync(serviceId, ct));

        // Clearing an entry that does not exist is a no-op, not an error.
        await store.ClearAsync(serviceId, tenantId, ct);
    }

    [Fact]
    public async Task Claim_is_single_flight_and_scoped_to_the_service()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store;
        var serviceId = $"svc-{Guid.NewGuid():N}"[..16];
        var otherServiceId = $"svc-{Guid.NewGuid():N}"[..16];
        var tenantId = $"sr-{Guid.NewGuid():N}"[..20];

        await store.RecordFailureAsync(serviceId, tenantId, "boom", ct);
        await store.RecordFailureAsync(otherServiceId, tenantId, "boom elsewhere", ct);

        var claimed = await store.TryClaimAsync(serviceId, "owner-a", Lease, NoWait, MaxAttempts, ct);
        Assert.NotNull(claimed);
        Assert.Equal(tenantId, claimed!.TenantId);
        Assert.Equal(serviceId, claimed.ServiceId);
        Assert.Equal("owner-a", claimed.LeaseOwner);

        // The lease keeps a second instance of the same service out ...
        Assert.Null(await store.TryClaimAsync(serviceId, "owner-b", Lease, NoWait, MaxAttempts, ct));

        // ... but never hides another service's entry for the same tenant: each service retries its own.
        var otherClaim = await store.TryClaimAsync(otherServiceId, "owner-c", Lease, NoWait, MaxAttempts, ct);
        Assert.NotNull(otherClaim);
        Assert.Equal(otherServiceId, otherClaim!.ServiceId);

        // Releasing makes it claimable again.
        await store.ReleaseLeaseAsync(serviceId, tenantId, "owner-a", ct);
        Assert.NotNull(await store.TryClaimAsync(serviceId, "owner-b", Lease, NoWait, MaxAttempts, ct));

        await store.ClearAsync(serviceId, tenantId, ct);
        await store.ClearAsync(otherServiceId, tenantId, ct);
    }

    [Fact]
    public async Task Retry_interval_and_attempt_budget_bound_the_claims()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = Store;
        var serviceId = $"svc-{Guid.NewGuid():N}"[..16];
        var tenantId = $"sr-{Guid.NewGuid():N}"[..20];

        await store.RecordFailureAsync(serviceId, tenantId, "boom", ct);

        // A freshly-failed entry is not retried immediately — it must sit out the retry interval, otherwise
        // a permanently failing tenant would be re-attempted on every tick.
        Assert.Null(await store.TryClaimAsync(serviceId, "owner", Lease, TimeSpan.FromMinutes(5), MaxAttempts, ct));
        Assert.NotNull(await store.TryClaimAsync(serviceId, "owner", Lease, NoWait, MaxAttempts, ct));

        // Once the attempt budget is exhausted the entry stays for operators but is no longer handed out.
        await store.ReleaseLeaseAsync(serviceId, tenantId, "owner", ct);
        Assert.Null(await store.TryClaimAsync(serviceId, "owner", Lease, NoWait, maxAttempts: 1, ct));
        Assert.Single(await store.ListAsync(serviceId, ct));

        await store.ClearAsync(serviceId, tenantId, ct);
    }
}
