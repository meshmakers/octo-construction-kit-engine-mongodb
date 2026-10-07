using System;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// Cuts the time range of an archive export into consecutive half-open slices
/// <c>[from, to)</c>, each read with one statement.
/// </summary>
/// <remarks>
/// <para>
/// The export used to page over the whole table with a keyset cursor (<c>ORDER BY key LIMIT n</c>
/// behind <c>key &gt; cursor</c>). Such a page is a top-n search over every row behind the cursor,
/// so its cost follows the size of the table and not the size of the page: on a 115 million row
/// archive a page of 5,000 rows took 0.4 s on a local node and 3.6 s on a three-node cluster,
/// against 0.05 s on a table of 700,000 rows. A statement bounded on both sides only looks at the
/// rows of its slice.
/// </para>
/// <para>
/// The protocol is propose, count, decide: <see cref="TryNext" /> proposes a slice, the caller
/// counts its rows (a range count is cheap) and hands the count to <see cref="Accept" />. A slice
/// with more than <see cref="Tolerance" /> times the target is refused and proposed again
/// narrower, from the same start; an accepted one is read, unless it is empty. So no statement
/// returns far more rows than the target, whatever the data looks like — an archive that is sparse
/// for a year and dense afterwards included, where a width extrapolated from the sparse part would
/// put months of the dense part into one statement. The only exception is a single instant that
/// holds more rows than that by itself: time cannot split it.
/// </para>
/// <para>
/// The accepted slices partition <c>[fromInclusive, toExclusive)</c> exactly: no gap, no overlap,
/// in ascending order. The width follows the data: after an accepted slice it is scaled by
/// target / rows, growing by at most <see cref="MaxGrowth" />; an empty slice grows it by
/// <see cref="MaxGrowth" />, so a gap costs a logarithmic number of counts and no read.
/// </para>
/// <para>
/// All instants are milliseconds since the Unix epoch, the precision CrateDB stores timestamps in.
/// </para>
/// </remarks>
internal sealed class ExportSlicePlanner
{
    /// <summary>
    /// A proposal is at most this many times wider than the slice before it, and a refused one is
    /// narrowed by at most this factor.
    /// </summary>
    internal const int MaxGrowth = 4;

    /// <summary>A slice is read when it holds at most this many times the target.</summary>
    internal const int Tolerance = 2;

    private readonly long _toExclusive;
    private readonly long _targetRows;
    private long _next;
    private long _width;
    private long _proposedTo;

    /// <param name="fromInclusive">Smallest instant to export, in milliseconds.</param>
    /// <param name="toExclusive">First instant behind the last one to export, in milliseconds.</param>
    /// <param name="rowCount">Rows in the range; only used to size the first proposal.</param>
    /// <param name="targetRows">Rows a slice aims at.</param>
    public ExportSlicePlanner(long fromInclusive, long toExclusive, long rowCount, long targetRows)
    {
        if (toExclusive < fromInclusive)
        {
            throw new ArgumentOutOfRangeException(nameof(toExclusive),
                "The end of the export range lies before its start.");
        }

        if (targetRows <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetRows), "The slice target must be positive.");
        }

        _next = fromInclusive;
        _proposedTo = fromInclusive;
        _toExclusive = toExclusive;
        _targetRows = targetRows;

        // Subtraction in decimal: the range of two longs can exceed a long.
        var range = (decimal)toExclusive - fromInclusive;
        _width = rowCount <= targetRows
            ? ToWidth(range)
            : ToWidth(Math.Ceiling(range * targetRows / rowCount));
    }

    /// <summary>
    /// Proposes the next slice, or returns <c>false</c> when the range is used up. Count the rows of
    /// the proposal and call <see cref="Accept" /> before asking again.
    /// </summary>
    public bool TryNext(out long fromInclusive, out long toExclusive)
    {
        if (_next >= _toExclusive)
        {
            fromInclusive = toExclusive = _toExclusive;
            return false;
        }

        fromInclusive = _next;
        // Compare the distance instead of adding: _next + _width can overflow.
        toExclusive = (decimal)_toExclusive - _next <= _width ? _toExclusive : _next + _width;
        _proposedTo = toExclusive;
        return true;
    }

    /// <summary>
    /// Decides on the slice last proposed, given the rows it holds. Returns <c>true</c> when the
    /// slice stands — read it if it has rows, then ask for the next — and <c>false</c> when it is
    /// too large: the next proposal starts at the same instant and is narrower.
    /// </summary>
    public bool Accept(long countedRows)
    {
        var width = (decimal)_proposedTo - _next;

        if (countedRows > _targetRows * (decimal)Tolerance && width > 1)
        {
            // Narrow down to what an even spread inside the slice would need, but by no more than
            // the growth limit. The count says how many rows the slice holds, not where they sit:
            // at the far edge of a gap they all sit at its end, and a slice cut down to the even
            // spread would be empty again and need many steps to grow back.
            var even = Math.Floor(width * _targetRows / countedRows);
            _width = ToWidth(Math.Max(even, Math.Floor(width / MaxGrowth)));
            return false;
        }

        _next = _proposedTo;

        var grown = width * MaxGrowth;
        _width = countedRows <= 0
            ? ToWidth(grown)
            : ToWidth(Math.Min(grown, Math.Ceiling(width * _targetRows / countedRows)));
        return true;
    }

    private static long ToWidth(decimal width) =>
        width < 1 ? 1 : width > long.MaxValue ? long.MaxValue : (long)width;
}
