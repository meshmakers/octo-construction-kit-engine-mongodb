using System;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Copy;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests.Copy;

/// <summary>AB#7274: which failures of a <c>COPY FROM</c> are worth a repeat.</summary>
public class CopyFailureClassifierTests
{
    [Theory]
    [InlineData("CircuitBreakingException[[parent] Data too large]")]
    [InlineData("Allocating 2mb for 'parent: copyFrom: 0' failed, breaker would use 1017mb in total")]
    [InlineData("[Parent] New used memory 2097152 [2mb] for [upsert request] would be larger than configured breaker: 1020054732")]
    [InlineData("EsRejectedExecutionException[rejected execution of coordinating operation]")]
    [InlineData("rejected execution of org.elasticsearch.transport")]
    [InlineData("NoShardAvailableActionException[null]")]
    [InlineData("UnavailableShardsException[primary shard is not active]")]
    [InlineData("NodeNotConnectedException[[node][127.0.0.1] Node not connected]")]
    public void TransientTexts_AreRecognised(string text)
    {
        Assert.True(CopyFailureClassifier.IsTransientText(text));
        Assert.Equal(LoadVerdict.Transient, CopyFailureClassifier.ClassifyFailure(
            new StreamDataCopyStatementException("COPY t FROM '***'", "XX000", text)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Column 'value' unknown")]
    [InlineData("Cannot read file: no such key")]
    [InlineData("AccessDenied: invalid credentials")]
    [InlineData("{\"parse error\": {\"count\": 3}}")]
    public void OtherTexts_AreNot(string text)
    {
        Assert.False(CopyFailureClassifier.IsTransientText(text));
        Assert.Equal(LoadVerdict.Permanent, CopyFailureClassifier.ClassifyFailure(
            new StreamDataCopyStatementException("COPY t FROM '***'", "XX000", text)));
    }

    [Fact]
    public void ATimeout_IsTransient_AnythingElseIsNot()
    {
        Assert.Equal(LoadVerdict.Transient, CopyFailureClassifier.ClassifyFailure(
            new StreamDataStatementTimeoutException(CrateStatementClass.Maintenance, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "COPY", null)));
        Assert.Equal(LoadVerdict.Permanent, CopyFailureClassifier.ClassifyFailure(new InvalidOperationException("breaker")));
    }

    [Theory]
    [InlineData(10, 0, 10, "Complete")]
    [InlineData(6, 0, 10, "Transient")]  // short, nothing counted: CrateDB does not count refused shard requests
    [InlineData(0, 0, 10, "Transient")]
    [InlineData(11, 0, 10, "Permanent")] // more than the dump holds is not a load problem
    public void SummaryWithoutErrors_IsDecidedByTheRowCount(long success, long errors, long expected, string verdict)
        => Assert.Equal(Enum.Parse<LoadVerdict>(verdict), CopyFailureClassifier.ClassifySummary(success, errors, expected, []));

    [Fact]
    public void SummaryWithErrors_IsTransientOnlyWhenEveryErrorTextIs()
    {
        Assert.Equal(LoadVerdict.Transient, CopyFailureClassifier.ClassifySummary(5, 5, 10,
            ["CircuitBreakingException[x]", "EsRejectedExecutionException[y]"]));
        Assert.Equal(LoadVerdict.Permanent, CopyFailureClassifier.ClassifySummary(5, 5, 10,
            ["CircuitBreakingException[x]", "parse error"]));
        Assert.Equal(LoadVerdict.Permanent, CopyFailureClassifier.ClassifySummary(5, 5, 10, []));
    }
}
