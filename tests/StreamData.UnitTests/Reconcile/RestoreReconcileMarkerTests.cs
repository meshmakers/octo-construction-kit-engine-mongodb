using System;
using System.Threading;
using System.Threading.Tasks;
using FakeItEasy;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Reconcile;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests.Reconcile;

/// <summary>AB#6968: the restore reconcile marker - statements, absent = not pending, and the short memo of the answer.</summary>
public class RestoreReconcileMarkerTests
{
    private const string Tenant = "acme-corp";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly IStreamDataDatabaseClient _db = A.Fake<IStreamDataDatabaseClient>();
    private readonly IStreamDataDatabaseManagementClient _mgmt = A.Fake<IStreamDataDatabaseManagementClient>();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 5, 20, 8, 0, 0, TimeSpan.Zero));
    private readonly RecomputeProcessState _state = new();

    private RestoreReconcileMarker Marker() => new(Tenant, _db, _mgmt, _state, _clock, NullLogger.Instance);

    private void Count(long rows) => A.CallTo(() => _db.GetCountAsync(Tenant, A<string>._, CrateStatementClass.Maintenance, A<CancellationToken>._))
        .Returns(rows);

    [Fact]
    public void Statements_NameEveryColumn_HaveNoDefault_AndKeyTheRowByTheFullTenantId()
    {
        var table = Marker().QualifiedTable;

        var create = RestoreReconcileMarker.BuildCreateTable(table);
        var upsert = RestoreReconcileMarker.BuildUpsert(table, "o'brien-corp", 1234);

        Assert.DoesNotContain("DEFAULT", create, StringComparison.Ordinal);
        Assert.Contains("\"tenant_id\" TEXT NOT NULL", create, StringComparison.Ordinal);
        Assert.Contains("(\"tenant_id\", \"marked_at_ms\") VALUES ('o''brien-corp', 1234)", upsert, StringComparison.Ordinal);
        Assert.EndsWith("DO UPDATE SET \"marked_at_ms\" = 1234;", upsert, StringComparison.Ordinal);
        Assert.Equal("acmecorp", TenantSchema.SchemaName(Tenant)); // '-' and '_' are stripped: tenants share a schema
        Assert.Contains("\"acmecorp\".\"__restore_reconcile\"", table, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoTable_IsNotPending()
    {
        A.CallTo(() => _db.GetCountAsync(Tenant, A<string>._, CrateStatementClass.Maintenance, A<CancellationToken>._))
            .Throws(new InvalidOperationException("RelationUnknown[Relation 'acmecorp.__restore_reconcile' unknown]"));

        Assert.False(await Marker().IsPendingAsync(Ct));
    }

    [Fact]
    public async Task Row_IsPending_AndTheAnswerIsRememberedForAShortTime()
    {
        Count(1);
        var marker = Marker();

        Assert.True(await marker.IsPendingAsync(Ct));
        Count(0); // cleared by another process
        Assert.True(await marker.IsPendingAsync(Ct)); // still the remembered answer
        _clock.Now += RestoreReconcileMarker.CacheTtl + TimeSpan.FromMilliseconds(1);
        Assert.False(await marker.IsPendingAsync(Ct)); // asked again
    }

    [Fact]
    public async Task Set_CreatesTheTableOnce_WritesTheRow_AndIsVisibleAtOnce()
    {
        Count(0);
        var marker = Marker();

        await marker.SetAsync(true, Ct);

        A.CallTo(() => _mgmt.ExecuteDdlAsync(Tenant, A<string>.That.StartsWith("CREATE TABLE IF NOT EXISTS"), Ct)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _db.ExecuteNonQueryAsync(Tenant, A<string>.That.StartsWith("INSERT INTO"), CrateStatementClass.Maintenance, Ct))
            .MustHaveHappenedOnceExactly();
        Assert.True(await marker.IsPendingAsync(Ct)); // without asking the database

        await marker.SetAsync(false, Ct);
        A.CallTo(() => _db.ExecuteNonQueryAsync(Tenant, A<string>.That.StartsWith("DELETE FROM"), CrateStatementClass.Maintenance, Ct))
            .MustHaveHappenedOnceExactly();
        Assert.False(await marker.IsPendingAsync(Ct));
        A.CallTo(() => _db.GetCountAsync(Tenant, A<string>._, CrateStatementClass.Maintenance, A<CancellationToken>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task Clear_WithoutATable_IsANoOp()
    {
        A.CallTo(() => _db.ExecuteNonQueryAsync(Tenant, A<string>._, CrateStatementClass.Maintenance, A<CancellationToken>._))
            .Throws(new InvalidOperationException("RelationUnknown[Relation 'acmecorp.__restore_reconcile' unknown]"));

        await Marker().SetAsync(false, Ct);

        Assert.False(await Marker().IsPendingAsync(Ct));
    }
}
