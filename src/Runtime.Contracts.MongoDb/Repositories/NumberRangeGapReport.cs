using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;

/// <summary>
///     The gap report of one number range (<c>System/AutoIncrement</c>) and one fiscal year
///     (<see cref="ITenantRepository.GetNumberRangeGapReportAsync" />): the counter values from <see cref="StartValue" />
///     to <see cref="CurrentValue" /> that no released document carries, and the values more than one document carries.
///     Computed from the documents the caller may read.
/// </summary>
/// <param name="NumberRange">The well-known name of the range (the <c>autoIncrementReference</c> of the documents).</param>
/// <param name="FiscalYear">The fiscal year of a range with fiscal-year periods; null for a range without periods.</param>
/// <param name="StartValue">The first number of the interval: the range's <c>StartValue</c>, or 1.</param>
/// <param name="CurrentValue">
///     The last number handed out: the fiscal year's period counter, or the counter of a range without periods. Below
///     <see cref="StartValue" /> when the range has handed out no number in that interval yet.
/// </param>
/// <param name="DocumentCount">The released documents read for the range and fiscal year, the irregular ones included.</param>
/// <param name="Missing">The counter values no document carries, as ascending, non-overlapping intervals.</param>
/// <param name="Duplicates">The counter values more than one document carries, ascending.</param>
/// <param name="Irregular">
///     Documents counted for the range and fiscal year whose number lies outside the interval, or whose number does not
///     have the range's format; at most <see cref="NumberRangeGapReportLimits.MaxIrregular" /> are listed.
/// </param>
/// <param name="IrregularCount">How many irregular documents there are (also those not listed).</param>
public sealed record NumberRangeGapReport(
    string NumberRange,
    int? FiscalYear,
    long StartValue,
    long CurrentValue,
    long DocumentCount,
    IReadOnlyList<NumberRangeGap> Missing,
    IReadOnlyList<NumberRangeDuplicate> Duplicates,
    IReadOnlyList<NumberRangeIrregularNumber> Irregular,
    long IrregularCount);

/// <summary>Counter values <paramref name="From" /> to <paramref name="To" /> (both included) no document carries.</summary>
/// <param name="From">First missing value.</param>
/// <param name="To">Last missing value.</param>
/// <param name="FromNumber">The formatted number of <paramref name="From" />.</param>
/// <param name="ToNumber">The formatted number of <paramref name="To" />.</param>
public sealed record NumberRangeGap(long From, long To, string FromNumber, string ToNumber);

/// <summary>A counter value more than one document carries, with every one of these documents.</summary>
/// <param name="SequenceValue">The counter value.</param>
/// <param name="Number">The formatted number.</param>
/// <param name="Documents">The documents that carry it.</param>
public sealed record NumberRangeDuplicate(long SequenceValue, string Number, IReadOnlyList<RtEntityId> Documents);

/// <summary>A document whose number lies outside the interval of the report or does not have the range's format.</summary>
/// <param name="Document">The document.</param>
/// <param name="Number">Its number as stored.</param>
/// <param name="SequenceValue">The counter value read from the number; null when the number does not have the format.</param>
public sealed record NumberRangeIrregularNumber(RtEntityId Document, string Number, long? SequenceValue);

/// <summary>Limits of the gap report.</summary>
public static class NumberRangeGapReportLimits
{
    /// <summary>Most irregular documents the report lists.</summary>
    public const int MaxIrregular = 1000;

    /// <summary>Largest interval (CurrentValue - StartValue + 1) the report checks.</summary>
    public const long MaxInterval = 100_000_000;
}
