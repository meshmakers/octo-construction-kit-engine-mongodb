using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Runtime.Engine.CrateDb.Client;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

// AB#8679: the retry of the rows CrateDB did not write in one archive write statement. The detection (a guarded row
// without RETURNING row, an unguarded affected-row shortfall) is ConflictOutcomeTests and the client; against a real
// CrateDB that drops rows without an error: StreamDataWriteLoss/ArchiveWriteShortfallIntegrationTests.
public class MissingRowRetryTests
{
    private const string Table = "\"tenant\".\"archive_1\"";

    private static readonly int[] Batch = [10, 11, 12, 13];

    /// <summary>Answers the statements in order and records the rows of each.</summary>
    private sealed class Statements(params MissingRowRetry.StatementOutcome[] outcomes)
    {
        public List<int[]> Calls { get; } = [];

        public Task<MissingRowRetry.StatementOutcome> ExecuteAsync(int[] rows)
        {
            Calls.Add(rows);
            return Task.FromResult(outcomes[Calls.Count - 1]);
        }
    }

    private static MissingRowRetry.StatementOutcome Outcome(int[] missing, params SkippedArchiveRow[] skipped)
        => new(skipped, missing);

    private static Task<IReadOnlyList<SkippedArchiveRow>> WriteAsync(Statements statements, bool guarded)
        => MissingRowRetry.WriteAsync(Batch, guarded, Table, statements.ExecuteAsync, NullLogger.Instance);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AStatementWithoutMissingRows_RunsOnce_AndReturnsItsSkippedRows(bool guarded)
    {
        var skipped = new SkippedArchiveRow(11, StreamDataSkipReason.LowerPrecedence);
        var statements = new Statements(Outcome([], skipped));

        var result = await WriteAsync(statements, guarded);

        Assert.Equal(Batch, Assert.Single(statements.Calls));
        Assert.Equal(skipped, Assert.Single(result));
    }

    [Fact]
    public async Task Guarded_MissingRows_AreWrittenOnceMore_OnlyThoseRows_AndTheSkippedRowsOfBothStatementsAreReported()
    {
        var statements = new Statements(
            Outcome([11, 13], new SkippedArchiveRow(12, StreamDataSkipReason.LowerPrecedence)),
            Outcome([], new SkippedArchiveRow(11, StreamDataSkipReason.ManualValueProtected)));

        var result = await WriteAsync(statements, guarded: true);

        Assert.Equal(2, statements.Calls.Count);
        Assert.Equal([11, 13], statements.Calls[1]);
        Assert.Equal([11, 12], result.Select(r => r.Index));
        Assert.Equal(StreamDataSkipReason.ManualValueProtected, result[0].Reason);
    }

    [Fact]
    public async Task Unguarded_AShortfall_RepeatsTheWholeStatement()
    {
        // The affected-row count does not say which rows are missing.
        var statements = new Statements(Outcome([10]), Outcome([]));

        var result = await WriteAsync(statements, guarded: false);

        Assert.Equal(2, statements.Calls.Count);
        Assert.Equal(Batch, statements.Calls[1]);
        Assert.Empty(result);
    }

    [Fact]
    public async Task Guarded_RowsStillMissingAfterTheRetry_FailTheWrite_NamingTableAndCounts()
    {
        var statements = new Statements(Outcome([11, 13]), Outcome([13]));

        var ex = await Assert.ThrowsAsync<StreamDataWriteIncompleteException>(() => WriteAsync(statements, guarded: true));

        Assert.Equal(2, statements.Calls.Count);
        Assert.Equal(Table, ex.QualifiedTable);
        Assert.Equal(2, ex.RowsSent);
        Assert.Equal(1, ex.RowsMissing);
        Assert.Contains("1 rows had no RETURNING row", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unguarded_AShortfallAfterTheRetry_FailsTheWrite_NamingTableAndCounts()
    {
        var statements = new Statements(Outcome([10, 11]), Outcome([10]));

        var ex = await Assert.ThrowsAsync<StreamDataWriteIncompleteException>(() => WriteAsync(statements, guarded: false));

        Assert.Equal(2, statements.Calls.Count);
        Assert.Equal(Table, ex.QualifiedTable);
        Assert.Equal(4, ex.RowsSent);
        Assert.Equal(1, ex.RowsMissing);
        Assert.Contains("CrateDB reported 3 affected rows for 4 rows sent", ex.Message, StringComparison.Ordinal);
    }
}
