using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Copy;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests.Copy;

/// <summary>
/// AB#5967: a COPY statement carries the storage credential, CrateDB records finished statements with their
/// text in sys.jobs_log, and only a filter that excludes COPY keeps the credential out of it. The staging
/// copy refuses to start without one.
/// </summary>
public class JobLogFilterGuardTests
{
    [Theory]
    [InlineData("stmt NOT ILIKE 'copy %'", true)]
    [InlineData("STMT not ilike 'COPY %'", true)]
    [InlineData("stmt   NOT   LIKE   'copy%'", true)]
    [InlineData("stmt NOT ILIKE 'copy %' AND stmt NOT ILIKE 'select 1'", true)]
    [InlineData("true", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("stmt ILIKE 'copy %'", false)]
    [InlineData("stmt NOT ILIKE 'select %'", false)]
    [InlineData("stmt NOT ILIKE 'copy %' OR duration > 1000", false)]
    public void IsFiltered_AcceptsOnlyAFilterThatExcludesCopy(string? filter, bool expected)
    {
        Assert.Equal(expected, JobLogFilterGuard.IsFiltered(filter));
    }

    [Fact]
    public async Task EnsureFiltered_ReadsTheSettingAndRefusesWithAMessageNamingIt()
    {
        var client = new ScriptedCopyClient(_ => Rows(("jobs_log_filter", "true")));
        var rt = OctoObjectId.GenerateNewId();

        var ex = await Assert.ThrowsAsync<ArchiveCopyRefusedException>(() =>
            JobLogFilterGuard.EnsureFilteredAsync(client, "t1", new ArchiveCopyOptions(), rt, CancellationToken.None));

        Assert.Equal(ArchiveCopyRefusalReason.JobLogNotFiltered, ex.Reason);
        Assert.Contains("stats.jobs_log_filter", ex.Message);
        Assert.Equal(rt, ex.ArchiveRtId);
        Assert.Contains("sys.cluster", client.Statements.Single().Reveal());
    }

    [Fact]
    public async Task EnsureFiltered_PassesWhenCopyIsExcluded()
    {
        var client = new ScriptedCopyClient(_ => Rows(("jobs_log_filter", JobLogFilterGuard.RecommendedFilter)));

        await JobLogFilterGuard.EnsureFilteredAsync(client, "t1", new ArchiveCopyOptions(), OctoObjectId.GenerateNewId(),
            CancellationToken.None);
    }

    [Fact]
    public async Task EnsureFiltered_RefusesWhenTheSettingCannotBeRead()
    {
        var client = new ScriptedCopyClient(_ => CrateStatementResult.Empty);

        await Assert.ThrowsAsync<ArchiveCopyRefusedException>(() =>
            JobLogFilterGuard.EnsureFilteredAsync(client, "t1", new ArchiveCopyOptions(), OctoObjectId.GenerateNewId(),
                CancellationToken.None));
    }

    internal static CrateStatementResult Rows(params (string Column, object? Value)[] row)
    {
        var dictionary = new Dictionary<string, object?>();
        foreach (var (column, value) in row)
        {
            dictionary[column] = value;
        }

        return new CrateStatementResult(0, [dictionary]);
    }
}
