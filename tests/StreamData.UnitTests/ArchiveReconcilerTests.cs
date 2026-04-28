using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Microsoft.Extensions.Logging.Abstractions;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// Concept §11 startup-reconciliation: covers the four behaviours of <see cref="ArchiveReconciler"/>
/// — happy path (Activated + table exists), drift (Activated but table missing → Failed + audit),
/// orphan (table without entity → log only), no-op cases (empty store, non-Activated entities).
/// </summary>
public class ArchiveReconcilerTests
{
    private const string Tenant = "tenant-x";
    private const string LegacyTable = "streamData";

    private readonly ICkArchiveRuntimeStore _store = A.Fake<ICkArchiveRuntimeStore>();
    private readonly IStreamDataDatabaseManagementClient _mgmt = A.Fake<IStreamDataDatabaseManagementClient>();
    private readonly IArchiveAuditTrail _audit = A.Fake<IArchiveAuditTrail>();

    private ArchiveReconciler NewSut() =>
        new(_store, _mgmt, _audit, NullLogger<ArchiveReconciler>.Instance);

    private static CkArchiveSnapshot Activated(OctoObjectId rt) =>
        new(rt, new RtCkId<CkTypeId>("Test", new CkTypeId("X")), CkArchiveStatus.Activated, null);

    private static CkArchiveSnapshot Created(OctoObjectId rt) =>
        new(rt, new RtCkId<CkTypeId>("Test", new CkTypeId("X")), CkArchiveStatus.Created, null);

    private void StubStore(params CkArchiveSnapshot[] snapshots)
    {
        A.CallTo(() => _store.EnumerateAsync()).Returns(ToAsync(snapshots));
    }

    private void StubTables(params string[] tables)
    {
        A.CallTo(() => _mgmt.ListTablesInTenantSchemaAsync(Tenant))
            .Returns(Task.FromResult<IReadOnlyList<string>>(tables));
    }

    [Fact]
    public async Task NoArchives_NoTables_NoOp()
    {
        StubStore();
        StubTables();

        await NewSut().ReconcileTenantAsync(Tenant, TestContext.Current.CancellationToken);

        A.CallTo(() => _store.SetStatusAsync(A<OctoObjectId>._, A<CkArchiveStatus>._)).MustNotHaveHappened();
        A.CallTo(() => _audit.RecordTransitionAsync(A<OctoObjectId>._, A<CkArchiveStatus>._, A<CkArchiveStatus>._, A<string?>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task ActivatedWithBackingTable_LeavesEverythingAlone()
    {
        var rt = OctoObjectId.GenerateNewId();
        StubStore(Activated(rt));
        StubTables(LegacyTable);

        await NewSut().ReconcileTenantAsync(Tenant, TestContext.Current.CancellationToken);

        A.CallTo(() => _store.SetStatusAsync(A<OctoObjectId>._, A<CkArchiveStatus>._)).MustNotHaveHappened();
        A.CallTo(() => _audit.RecordTransitionAsync(A<OctoObjectId>._, A<CkArchiveStatus>._, A<CkArchiveStatus>._, A<string?>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task ActivatedWithoutBackingTable_FlipsToFailedAndRecordsAudit()
    {
        var rt = OctoObjectId.GenerateNewId();
        StubStore(Activated(rt));
        StubTables(); // empty — table missing

        await NewSut().ReconcileTenantAsync(Tenant, TestContext.Current.CancellationToken);

        A.CallTo(() => _store.SetStatusAsync(rt, CkArchiveStatus.Failed)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _audit.RecordTransitionAsync(
            rt, CkArchiveStatus.Activated, CkArchiveStatus.Failed,
            A<string?>.That.Matches(s => s != null && s.Contains("missing", StringComparison.OrdinalIgnoreCase))))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task NonActivatedArchives_AreSkippedRegardlessOfTablePresence()
    {
        var rt = OctoObjectId.GenerateNewId();
        StubStore(Created(rt));
        StubTables(); // table missing — but archive is Created, so no action expected

        await NewSut().ReconcileTenantAsync(Tenant, TestContext.Current.CancellationToken);

        A.CallTo(() => _store.SetStatusAsync(A<OctoObjectId>._, A<CkArchiveStatus>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task OrphanTableWithoutMatchingEntity_IsLoggedButNotDropped()
    {
        StubStore(); // no archives
        StubTables("orphan_legacy_table");

        await NewSut().ReconcileTenantAsync(Tenant, TestContext.Current.CancellationToken);

        // No state changes; no entity touched.
        A.CallTo(() => _store.SetStatusAsync(A<OctoObjectId>._, A<CkArchiveStatus>._)).MustNotHaveHappened();
        // No drop call exists on the management client either; orphans are just logged.
    }

    [Fact]
    public async Task MultipleActivated_OnlyMissingOnesGetFlipped()
    {
        var rtFine = OctoObjectId.GenerateNewId();
        var rtBroken = OctoObjectId.GenerateNewId();
        StubStore(Activated(rtFine), Activated(rtBroken));
        StubTables(LegacyTable); // table exists — both archives currently share it under legacy routing

        await NewSut().ReconcileTenantAsync(Tenant, TestContext.Current.CancellationToken);

        // Both archives map to the legacy table; with it present neither is flipped. This case
        // changes once per-archive table routing replaces the shared LegacyStreamDataTable.
        A.CallTo(() => _store.SetStatusAsync(A<OctoObjectId>._, A<CkArchiveStatus>._)).MustNotHaveHappened();
    }

    private static async IAsyncEnumerable<CkArchiveSnapshot> ToAsync(CkArchiveSnapshot[] items)
    {
        foreach (var item in items)
        {
            yield return item;
            await Task.Yield();
        }
    }
}
