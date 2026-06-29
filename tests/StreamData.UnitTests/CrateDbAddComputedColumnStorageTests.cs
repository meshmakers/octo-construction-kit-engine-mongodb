using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// <c>AddComputedColumnStorageAsync</c> (AB#4189 Phase 7): emits the <c>ALTER TABLE … ADD COLUMN</c>
/// for a computed column typed from its ResultType, and is idempotent — a benign "column already
/// exists" failure (CrateDB has no ADD COLUMN IF NOT EXISTS) is swallowed so a re-add reuses the
/// orphaned column.
/// </summary>
public class CrateDbAddComputedColumnStorageTests
{
    private static readonly OctoObjectId Archive = OctoObjectId.GenerateNewId();
    private static readonly RtCkId<CkTypeId> Type = new("Test", new CkTypeId("EnergyMeter"));

    private readonly IStreamDataDatabaseClient _db = A.Fake<IStreamDataDatabaseClient>();
    private readonly IStreamDataDatabaseManagementClient _mgmt = A.Fake<IStreamDataDatabaseManagementClient>();
    private readonly ICkCacheService _cache = A.Fake<ICkCacheService>();
    private readonly IArchiveRuntimeStore _store = A.Fake<IArchiveRuntimeStore>();

    private static readonly IFormulaEngine Formula =
        new ServiceCollection().AddFormulaEngine().BuildServiceProvider().GetRequiredService<IFormulaEngine>();

    private static readonly IOptions<StreamDataConfiguration> Config =
        Options.Create(new StreamDataConfiguration { ConnectionString = "Host=ignored" });

    private CrateDbStreamDataRepository NewSut() =>
        new(NullLogger<CrateDbStreamDataRepository>.Instance, _cache, _db, _mgmt, Config, "tenant-x", _store, Formula);

    private static CkArchiveColumnSpec Comp(string name) =>
        new(string.Empty, Indexed: true, Required: false)
        {
            Name = name,
            Formula = "activepower / apparentpower",
            ResultType = FormulaResultType.Double,
            ComputedState = ComputedColumnState.Pending,
        };

    private static ArchiveSnapshot Snapshot(params CkArchiveColumnSpec[] columns) =>
        new(Archive, Type, CkArchiveStatus.Activated, "energy", columns);

    [Fact]
    public async Task AddStorage_EmitsAlterAddColumn_TypedFromResultType()
    {
        string? executed = null;
        A.CallTo(() => _db.ExecuteNonQueryAsync("tenant-x", A<string>._, A<CancellationToken>._))
            .Invokes(call => executed = call.GetArgument<string>(1))
            .Returns(1);

        await NewSut().AddComputedColumnStorageAsync(Snapshot(Comp("powerFactor")), "powerFactor", CancellationToken.None);

        Assert.NotNull(executed);
        Assert.Contains("ALTER TABLE", executed);
        Assert.Contains("ADD COLUMN", executed);
        Assert.Contains("\"powerfactor\"", executed);
        Assert.Contains("DOUBLE PRECISION", executed);
    }

    [Fact]
    public async Task AddStorage_ColumnAlreadyExists_IsSwallowed()
    {
        A.CallTo(() => _db.ExecuteNonQueryAsync("tenant-x", A<string>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("The table already has a column named powerfactor"));

        // Must not throw — idempotent re-add over an orphaned column.
        await NewSut().AddComputedColumnStorageAsync(Snapshot(Comp("powerFactor")), "powerFactor", CancellationToken.None);
    }

    [Fact]
    public async Task AddStorage_RealFailure_Propagates()
    {
        A.CallTo(() => _db.ExecuteNonQueryAsync("tenant-x", A<string>._, A<CancellationToken>._))
            .Throws(new InvalidOperationException("connection refused"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewSut().AddComputedColumnStorageAsync(Snapshot(Comp("powerFactor")), "powerFactor", CancellationToken.None));
    }

    [Fact]
    public async Task AddStorage_UnknownColumn_IsNoOp()
    {
        await NewSut().AddComputedColumnStorageAsync(Snapshot(Comp("powerFactor")), "other", CancellationToken.None);

        A.CallTo(() => _db.ExecuteNonQueryAsync(A<string>._, A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    private static CkArchiveColumnSpec CompRef(string name, string formula) =>
        new(string.Empty, Indexed: true, Required: false)
        {
            Name = name, Formula = formula, ResultType = FormulaResultType.Double,
            ComputedState = ComputedColumnState.Active,
        };

    [Fact]
    public async Task AddPendingStorage_AltersVersionedPendingColumn_WhenNotReferenced()
    {
        string? executed = null;
        A.CallTo(() => _db.ExecuteNonQueryAsync("tenant-x", A<string>._, A<CancellationToken>._))
            .Invokes(call => executed = call.GetArgument<string>(1))
            .Returns(1);

        await NewSut().AddPendingComputedColumnStorageAsync(Snapshot(CompRef("powerFactor", "x / y")), "powerFactor",
            CancellationToken.None);

        Assert.NotNull(executed);
        Assert.Contains("ADD COLUMN", executed);
        Assert.Contains("\"powerfactor__v1\"", executed);
    }

    [Fact]
    public async Task AddPendingStorage_RejectsWhenReferencedByAnotherComputedColumn()
    {
        // ratio references powerfactor by its physical name; re-versioning powerFactor would orphan
        // that reference, so the formula change is rejected (AB#4189 Phase 7 MVP guard).
        var snapshot = Snapshot(
            CompRef("powerFactor", "activepower / apparentpower"),
            CompRef("ratio", "powerfactor * 2"));

        await Assert.ThrowsAsync<ComputedColumnInvalidException>(() =>
            NewSut().AddPendingComputedColumnStorageAsync(snapshot, "powerFactor", CancellationToken.None));

        A.CallTo(() => _db.ExecuteNonQueryAsync(A<string>._, A<string>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }
}
