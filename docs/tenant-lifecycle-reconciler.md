# Tenant lifecycle: stalled-tenant reconciler claim (AB#4348, AB#8117)

`ITenantLifecycleStore` keeps one record per tenant (state `Creating` / `Active` / `Failed` / `Deleting`).
A reconciler in the owning service claims `Creating` tenants through `TryClaimForReconcileAsync` and
re-drives their setup. The claim is a lease: `LeaseOwner` / `LeaseUntil`, `AttemptCount` is bumped per claim.

## Minimum age (AB#8117)

`TryClaimForReconcileAsync(owner, leaseDuration, minimumAge, ct)` only claims tenants whose creation
cycle (`CreatedUtc`) is at least `minimumAge` old. A brand-new tenant is still being set up by the normal
path; claiming it immediately raced that path, failed on "not seeded yet" and parked the tenant behind the
lease. `null` / zero disables the filter, a record without `CreatedUtc` counts as old.

`EnsureCreatingAsync` restarts `CreatedUtc` when a new creation cycle begins (missing or `Failed` record);
an in-flight `Creating` cycle and `Active` / `Deleting` records keep their original creation time.

`MarkActiveAsync` never looks at the lease: the normal setup path completes a tenant to `Active` and clears
the lease even while the reconciler holds one.
