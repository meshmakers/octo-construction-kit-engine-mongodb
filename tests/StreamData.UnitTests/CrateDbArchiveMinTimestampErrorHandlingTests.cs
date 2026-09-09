using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// AB#4284: <c>GetArchiveMinTimestampAsync</c> must only swallow a genuine "no backing table" read
/// (→ null / empty source) and let every other read failure propagate, so a transient CrateDB read
/// during a backfill ends the job Failed with the real error instead of a misleading "source holds
/// no data" no-op. These tests exercise the classifier that gates the catch filter.
/// </summary>
/// <remarks>
/// AB#5157: the coverage probe <c>GetArchiveCoverageAsync</c> shares this classifier under the
/// identical <c>when (IsRelationUnknown(ex))</c> filter — "no backing table" reads as no coverage
/// (null), every other failure propagates so the coverage cache never memoises a transient error.
/// </remarks>
public class CrateDbArchiveMinTimestampErrorHandlingTests
{
    [Fact]
    public void IsRelationUnknown_CoverageProbeSharesClassifier_MissingTableTrue_TransientFalse()
    {
        // Same two verdicts the coverage probe relies on: a missing relation is "empty", a
        // transient read failure is not.
        Assert.True(CrateDbStreamDataRepository.IsRelationUnknown(
            new Exception("RelationUnknown[Relation 'octo_t1.archive_cov' unknown]")));
        Assert.False(CrateDbStreamDataRepository.IsRelationUnknown(
            new IOException("Exception while reading from stream")));
    }

    [Theory]
    [InlineData("RelationUnknown[Relation 'octo_t1.archive_x' unknown]")]
    [InlineData("Relation 'octo_t1.archive_x' unknown")]
    [InlineData("io.crate.exceptions.RelationUnknown: unknown relation")]
    [InlineData("relation \"archive_x\" does not exist")]
    public void IsRelationUnknown_TableMissingMessages_ReturnsTrue(string message)
    {
        Assert.True(CrateDbStreamDataRepository.IsRelationUnknown(new Exception(message)));
    }

    [Fact]
    public void IsRelationUnknown_WrappedInnerException_ReturnsTrue()
    {
        var inner = new Exception("RelationUnknown[Relation 'octo_t1.archive_x' unknown]");
        var outer = new InvalidOperationException("query failed", inner);

        Assert.True(CrateDbStreamDataRepository.IsRelationUnknown(outer));
    }

    [Theory]
    [InlineData("Exception while reading from stream")]
    [InlineData("Connection reset by peer")]
    [InlineData("Timeout during reading attempt")]
    [InlineData("57014: canceling statement due to statement timeout")]
    public void IsRelationUnknown_TransientReadFailures_ReturnsFalse(string message)
    {
        // These are the failures that MUST propagate — not be masked as an empty source.
        Assert.False(CrateDbStreamDataRepository.IsRelationUnknown(new Exception(message)));
        Assert.False(CrateDbStreamDataRepository.IsRelationUnknown(new IOException(message)));
    }

    // ---- AB#5157 E3: a tenant without a CrateDB schema yet reads as "no table" ----

    [Theory]
    [InlineData("XX000: Schema 'octot1' unknown")]
    [InlineData("SchemaUnknown[Schema 'octot1' unknown]")]
    [InlineData("Schema octot1 unknown")]
    public void IsSchemaUnknown_SchemaMissingMessages_ReturnsTrue_AndIsMissingTableErrorFollows(string message)
    {
        var ex = new Exception(message);

        Assert.True(CrateDbStreamDataRepository.IsSchemaUnknown(ex));
        Assert.True(CrateDbStreamDataRepository.IsMissingTableError(ex));
        // The relation classifier alone does NOT cover it — that was the E3 defect: the probes
        // propagated "Schema unknown" instead of returning null.
        Assert.False(CrateDbStreamDataRepository.IsRelationUnknown(ex));
    }

    [Fact]
    public void IsSchemaUnknown_WrappedInnerException_ReturnsTrue()
    {
        var inner = new Exception("XX000: Schema 'octot1' unknown");
        var outer = new InvalidOperationException("query failed", inner);

        Assert.True(CrateDbStreamDataRepository.IsSchemaUnknown(outer));
        Assert.True(CrateDbStreamDataRepository.IsMissingTableError(outer));
    }

    [Theory]
    [InlineData("Exception while reading from stream")]
    [InlineData("Connection reset by peer")]
    [InlineData("Timeout during reading attempt")]
    [InlineData("57014: canceling statement due to statement timeout")]
    [InlineData("unknown error while writing schema-less document")]
    // A missing COLUMN whose name happens to start with "schema" is not a missing tenant schema.
    [InlineData("ColumnUnknown[Column 'schema_version' unknown]")]
    [InlineData("ColumnUnknown[Column schema_version unknown]")]
    public void IsMissingTableError_TransientReadFailures_ReturnsFalse(string message)
    {
        // Neither classifier may mask a transient failure as an empty source.
        Assert.False(CrateDbStreamDataRepository.IsSchemaUnknown(new Exception(message)));
        Assert.False(CrateDbStreamDataRepository.IsMissingTableError(new Exception(message)));
        Assert.False(CrateDbStreamDataRepository.IsMissingTableError(new IOException(message)));
    }

    [Fact]
    public void IsMissingTableError_RelationUnknown_StillTrue()
    {
        Assert.True(CrateDbStreamDataRepository.IsMissingTableError(
            new Exception("RelationUnknown[Relation 'octo_t1.archive_x' unknown]")));
    }

    // ---- the probes themselves ----

    private static readonly OctoObjectId Archive = OctoObjectId.GenerateNewId();
    private static readonly RtCkId<CkTypeId> SomeType = new("Test", new CkTypeId("TempSensor"));

    private sealed class ProbeHarness
    {
        public readonly IStreamDataDatabaseClient Db = A.Fake<IStreamDataDatabaseClient>();
        public readonly IArchiveRuntimeStore Store = A.Fake<IArchiveRuntimeStore>();

        public ProbeHarness()
        {
            A.CallTo(() => Store.GetAsync(Archive))
                .Returns(new ArchiveSnapshot(Archive, SomeType, CkArchiveStatus.Activated, null, Array.Empty<CkArchiveColumnSpec>()));
        }

        public void ProbeThrows(Exception ex) =>
            A.CallTo(() => Db.StreamRawRowsAsync("tenant-x", A<string>._, A<CancellationToken>._)).Throws(ex);

        public CrateDbStreamDataRepository NewSut() =>
            new(NullLogger<CrateDbStreamDataRepository>.Instance,
                A.Fake<ICkCacheService>(), Db, A.Fake<IStreamDataDatabaseManagementClient>(),
                Options.Create(new StreamDataConfiguration { ConnectionString = "Host=ignored" }),
                "tenant-x", Store, A.Fake<IFormulaEngine>());
    }

    [Fact]
    public async Task GetArchiveCoverageAsync_SchemaUnknown_ReturnsNull()
    {
        var h = new ProbeHarness();
        h.ProbeThrows(new Exception("XX000: Schema 'tenantx' unknown"));

        var coverage = await h.NewSut().GetArchiveCoverageAsync(Archive, TestContext.Current.CancellationToken);

        Assert.Null(coverage);
    }

    [Fact]
    public async Task GetArchiveMinTimestampAsync_SchemaUnknown_ReturnsNull()
    {
        var h = new ProbeHarness();
        h.ProbeThrows(new Exception("XX000: Schema 'tenantx' unknown"));

        var min = await h.NewSut().GetArchiveMinTimestampAsync(Archive, TestContext.Current.CancellationToken);

        Assert.Null(min);
    }

    [Fact]
    public async Task GetArchiveCoverageAsync_TransientReadFailure_Propagates()
    {
        var h = new ProbeHarness();
        h.ProbeThrows(new IOException("Exception while reading from stream"));

        await Assert.ThrowsAsync<IOException>(() => h.NewSut().GetArchiveCoverageAsync(Archive, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetArchiveMinTimestampAsync_TransientReadFailure_Propagates()
    {
        var h = new ProbeHarness();
        h.ProbeThrows(new IOException("Exception while reading from stream"));

        await Assert.ThrowsAsync<IOException>(() => h.NewSut().GetArchiveMinTimestampAsync(Archive, TestContext.Current.CancellationToken));
    }
}
