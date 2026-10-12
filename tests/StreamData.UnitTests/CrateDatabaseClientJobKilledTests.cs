using System;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Client;
using Xunit;
using Cause = Meshmakers.Octo.Runtime.Engine.CrateDb.Client.CrateDatabaseClient.JobKilledCause;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// CrateDB answers every cancelled statement with <c>XX000: Job killed</c>; the client has to say
/// who asked for it (AB#6283). The decision is a pure function, tested here without a database.
/// </summary>
public class CrateDatabaseClientJobKilledTests
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(120);

    [Fact]
    public void CallerCancelled_IsACancellation_WhateverElseIsTrue()
    {
        Assert.Equal(Cause.CallerCancelled,
            CrateDatabaseClient.ClassifyJobKilled(true, false, TimeSpan.FromSeconds(1), CommandTimeout));
        Assert.Equal(Cause.CallerCancelled,
            CrateDatabaseClient.ClassifyJobKilled(true, true, TimeSpan.FromSeconds(200), CommandTimeout));
    }

    [Fact]
    public void AttemptRanForItsCommandTimeout_IsTheBudget()
    {
        Assert.Equal(Cause.BudgetElapsed,
            CrateDatabaseClient.ClassifyJobKilled(false, false, CommandTimeout, CommandTimeout));
        Assert.Equal(Cause.BudgetElapsed,
            CrateDatabaseClient.ClassifyJobKilled(false, false, CommandTimeout + TimeSpan.FromMilliseconds(40), CommandTimeout));
    }

    [Fact]
    public void KilledByTheCallWideTimeout_BeforeItsOwnCommandTimeout_IsTheBudgetToo()
    {
        // A retried attempt, or one that waited long for its connection, starts late: the pipeline's
        // one timeout for the whole call ends it while the attempt itself is still young. Without
        // this the caller would get the raw "Job killed" instead of a statement timeout.
        Assert.Equal(Cause.BudgetElapsed,
            CrateDatabaseClient.ClassifyJobKilled(false, true, TimeSpan.FromSeconds(30), CommandTimeout));
    }

    [Fact]
    public void KilledByAnyoneElse_PassesThrough()
    {
        Assert.Equal(Cause.Foreign,
            CrateDatabaseClient.ClassifyJobKilled(false, false, TimeSpan.FromSeconds(30), CommandTimeout));
    }
}
