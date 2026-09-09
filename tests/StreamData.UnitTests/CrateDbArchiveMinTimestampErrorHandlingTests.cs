using System;
using System.IO;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
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
}
