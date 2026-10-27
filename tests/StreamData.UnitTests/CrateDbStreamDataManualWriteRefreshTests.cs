using System;
using System.Collections.Generic;
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
/// A write that contains manual rows refreshes the archive table, so the correction is visible to the next
/// read; automated batches keep CrateDB's asynchronous refresh. The read-after-write against a real CrateDB is
/// covered in the asset repository (<c>StreamDataInsertTimeRangeRestTests</c>).
/// </summary>
public class CrateDbStreamDataManualWriteRefreshTests
{
    private static readonly OctoObjectId Archive = OctoObjectId.GenerateNewId();
    private static readonly RtCkId<CkTypeId> SomeType = new("Test", new CkTypeId("EnergyMeter"));

    private readonly IStreamDataDatabaseClient _db = A.Fake<IStreamDataDatabaseClient>();
    private readonly IStreamDataDatabaseManagementClient _mgmt = A.Fake<IStreamDataDatabaseManagementClient>();
    private readonly ICkCacheService _cache = A.Fake<ICkCacheService>();
    private readonly IArchiveRuntimeStore _store = A.Fake<IArchiveRuntimeStore>();

    private static readonly IFormulaEngine Formula =
        new ServiceCollection().AddFormulaEngine().BuildServiceProvider().GetRequiredService<IFormulaEngine>();

    private static readonly IOptions<StreamDataConfiguration> Config =
        Options.Create(new StreamDataConfiguration { ConnectionString = "Host=ignored" });

    public CrateDbStreamDataManualWriteRefreshTests()
    {
        A.CallTo(() => _store.GetAsync(Archive)).Returns(new ArchiveSnapshot(Archive, SomeType, CkArchiveStatus.Activated,
            "energy", [new CkArchiveColumnSpec("activePower", Indexed: true, Required: false)]));
        A.CallTo(() => _db.InsertDataAsync("tenant-x", A<string>._, A<IReadOnlyList<string>>._, A<IEnumerable<DataPointDto>>._, A<IReadOnlyList<ArchiveConflictKey>?>._))
            .Returns(Task.FromResult<IReadOnlyList<SkippedArchiveRow>>([]));
    }

    private CrateDbStreamDataRepository NewSut() =>
        new(NullLogger<CrateDbStreamDataRepository>.Instance, _cache, _db, _mgmt, Config, "tenant-x", _store, Formula);

    private static StreamDataPoint Point(ArchiveRowOrigin origin) => new()
    {
        RtId = OctoObjectId.GenerateNewId(),
        CkTypeId = SomeType,
        Timestamp = new DateTime(2026, 6, 28, 12, 0, 0, DateTimeKind.Utc),
        Attributes = new Dictionary<string, object?> { ["activePower"] = 8.0 },
        Origin = origin,
        ManualWrittenBy = origin == ArchiveRowOrigin.Manual ? "jane.doe" : null
    };

    [Fact]
    public async Task AWriteWithAManualRow_RefreshesTheTable_AfterTheInsert()
    {
        await NewSut().InsertAsync(Archive, [Point(ArchiveRowOrigin.Automated), Point(ArchiveRowOrigin.Manual)]);

        A.CallTo(() => _db.InsertDataAsync("tenant-x", A<string>._, A<IReadOnlyList<string>>._, A<IEnumerable<DataPointDto>>._, A<IReadOnlyList<ArchiveConflictKey>?>._))
            .MustHaveHappenedOnceExactly()
            .Then(A.CallTo(() => _db.RefreshArchiveTableAsync("tenant-x", A<string>.That.Contains(Archive.ToString())))
                .MustHaveHappenedOnceExactly());
    }

    [Fact]
    public async Task AnAutomatedWrite_DoesNotRefreshTheTable()
    {
        await NewSut().InsertAsync(Archive, [Point(ArchiveRowOrigin.Automated), Point(ArchiveRowOrigin.Automated)]);

        A.CallTo(() => _db.RefreshArchiveTableAsync(A<string>._, A<string>._)).MustNotHaveHappened();
    }
}
