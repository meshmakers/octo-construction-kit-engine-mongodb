using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Client;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Dtos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// AB#7656: a raw archive ranks by <c>$Origin</c> first unless it names the key itself, so a manual value is protected
/// by default; <see cref="ConflictKeyOrder.Ignore" /> on <c>$Origin</c> opts out. The time-range path and the stored outcome against a real CrateDB are covered in
/// <c>ArchiveRowOriginIntegrationTests</c>.
/// </summary>
public class CrateDbStreamDataOriginFirstTests
{
    private static readonly OctoObjectId Archive = OctoObjectId.GenerateNewId();
    private static readonly RtCkId<CkTypeId> SomeType = new("Test", new CkTypeId("EnergyMeter"));
    private static readonly ArchiveConflictKey Origin = new(ArchiveConflictKey.OriginKey, ConflictKeyOrder.HigherWins);

    private readonly IStreamDataDatabaseClient _db = A.Fake<IStreamDataDatabaseClient>();
    private readonly IArchiveRuntimeStore _store = A.Fake<IArchiveRuntimeStore>();
    private IReadOnlyList<ArchiveConflictKey>? _passed;

    private static readonly IFormulaEngine Formula =
        new ServiceCollection().AddFormulaEngine().BuildServiceProvider().GetRequiredService<IFormulaEngine>();

    private static readonly IOptions<StreamDataConfiguration> Config =
        Options.Create(new StreamDataConfiguration { ConnectionString = "Host=ignored" });

    public CrateDbStreamDataOriginFirstTests()
    {
        A.CallTo(() => _db.InsertDataAsync("tenant-x", A<string>._, A<IReadOnlyList<string>>._, A<IEnumerable<DataPointDto>>._,
                A<IReadOnlyList<ArchiveConflictKey>?>._))
            .Invokes((string _, string _, IReadOnlyList<string> _, IEnumerable<DataPointDto> _, IReadOnlyList<ArchiveConflictKey>? p) => _passed = p)
            .Returns(Task.FromResult<IReadOnlyList<SkippedArchiveRow>>([]));
    }

    private CrateDbStreamDataRepository NewSut(IReadOnlyList<ArchiveConflictKey> precedence)
    {
        A.CallTo(() => _store.GetAsync(Archive)).Returns(new ArchiveSnapshot(Archive, SomeType, CkArchiveStatus.Activated,
            "energy", [new CkArchiveColumnSpec("activePower", Indexed: true, Required: false),
                new CkArchiveColumnSpec("dataQuality", Indexed: false, Required: false)])
        {
            ConflictPrecedence = precedence
        });
        return new CrateDbStreamDataRepository(NullLogger<CrateDbStreamDataRepository>.Instance, A.Fake<ICkCacheService>(), _db,
            A.Fake<IStreamDataDatabaseManagementClient>(), Config, "tenant-x", _store, Formula);
    }

    private static StreamDataPoint Point(ArchiveRowOrigin origin = ArchiveRowOrigin.Automated) => new()
    {
        RtId = OctoObjectId.GenerateNewId(),
        CkTypeId = SomeType,
        Timestamp = new DateTime(2026, 6, 28, 12, 0, 0, DateTimeKind.Utc),
        Attributes = new Dictionary<string, object?> { ["activePower"] = 8.0, ["dataQuality"] = 1 },
        Origin = origin,
        ManualWrittenBy = origin == ArchiveRowOrigin.Manual ? "jane.doe" : null
    };

    [Fact]
    public async Task ARawArchiveWithoutPrecedence_RanksByTheOriginAlone()
    {
        await NewSut([]).InsertAsync(Archive, [Point()]);

        Assert.Equal([Origin], _passed);
    }

    [Fact]
    public async Task ARawArchiveThatLeavesTheOriginOut_GetsItPrependedBeforeItsOwnKeys()
    {
        await NewSut([new ArchiveConflictKey("dataQuality", ConflictKeyOrder.LowerWins)]).InsertAsync(Archive, [Point()]);

        Assert.Equal([Origin, new ArchiveConflictKey("dataquality", ConflictKeyOrder.LowerWins)], _passed);
    }

    [Fact]
    public async Task ARawArchiveThatAlreadyRanksByTheOriginFirst_IsUsedAsConfigured()
    {
        await NewSut([Origin, new ArchiveConflictKey("dataQuality", ConflictKeyOrder.LowerWins)]).InsertAsync(Archive, [Point()]);

        Assert.Equal([Origin, new ArchiveConflictKey("dataquality", ConflictKeyOrder.LowerWins)], _passed);
    }

    [Fact]
    public async Task ARawArchiveWithTheOriginAfterAnotherKey_KeepsTheAuthorsOrder()
    {
        await NewSut([new ArchiveConflictKey("dataQuality", ConflictKeyOrder.LowerWins), Origin]).InsertAsync(Archive, [Point()]);

        Assert.Equal([new ArchiveConflictKey("dataquality", ConflictKeyOrder.LowerWins), Origin], _passed);
    }

    [Fact]
    public async Task ARawArchiveThatIgnoresTheOrigin_RanksByItsOwnKeysOnly()
    {
        await NewSut([new ArchiveConflictKey(ArchiveConflictKey.OriginKey, ConflictKeyOrder.Ignore),
            new ArchiveConflictKey("dataQuality", ConflictKeyOrder.LowerWins)]).InsertAsync(Archive, [Point()]);

        Assert.Equal([new ArchiveConflictKey("dataquality", ConflictKeyOrder.LowerWins)], _passed);
    }

    [Fact]
    public async Task ARawArchiveThatOnlyIgnoresTheOrigin_HasNoGuard()
    {
        _passed = [Origin];

        await NewSut([new ArchiveConflictKey(ArchiveConflictKey.OriginKey, ConflictKeyOrder.Ignore)]).InsertAsync(Archive, [Point()]);

        Assert.Null(_passed);
    }

    [Fact]
    public async Task AManualRowTheDatabaseDidNotStore_FailsTheWrite_NeverReportsSuccess()
    {
        // The origin ranking makes this unreachable for a configured archive; the check stays as the safety net.
        A.CallTo(() => _db.InsertDataAsync("tenant-x", A<string>._, A<IReadOnlyList<string>>._, A<IEnumerable<DataPointDto>>._,
                A<IReadOnlyList<ArchiveConflictKey>?>._))
            .Returns(Task.FromResult<IReadOnlyList<SkippedArchiveRow>>([new SkippedArchiveRow(0, StreamDataSkipReason.LowerPrecedence)]));
        var error = await Assert.ThrowsAsync<ManualStreamDataNotAppliedException>(() =>
            NewSut([]).InsertAsync(Archive, [Point(ArchiveRowOrigin.Manual), Point()]));

        Assert.Equal(1, error.Result.Stored);
        Assert.Contains("1 manual row(s) were not stored", error.Message);
    }
}
