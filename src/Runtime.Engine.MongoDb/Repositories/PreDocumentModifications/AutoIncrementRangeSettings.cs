using System.Globalization;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.PreDocumentModifications;

/// <summary>
///     The configuration of one <c>System/AutoIncrement</c> number range (System 2.9.0, AB#9754): its format, whether the
///     counter restarts per fiscal year, the fiscal-year start month and the reference time zone. Read from the counter
///     entity and checked with <see cref="TryCreate" />, both when a range is configured (insert or replace of the
///     counter) and before numbers are allocated from it, so a range that is not valid never hands out a number.
/// </summary>
/// <remarks>
///     Rules (design note <c>docs/concept-number-ranges-and-released-documents.md</c>, D2 and D4):
///     <list type="bullet">
///         <item><c>PeriodKind = FiscalYear</c> needs a <c>ReferenceTimeZone</c> that is a known IANA id; a missing or
///         unknown zone is an error, never a silent UTC default.</item>
///         <item><c>FiscalYearStartMonth</c> is 1-12 (absent: 1, fiscal year = calendar year).</item>
///         <item>A token format must parse; <c>{yyyy}</c> / <c>{yy}</c> need <c>PeriodKind = FiscalYear</c>, because a
///         range without periods has no fiscal year to show. The other way round, <c>PeriodKind = FiscalYear</c>
///         needs a token format with <c>{yyyy}</c> or <c>{yy}</c> (no format and a legacy format are refused): its
///         counter restarts every fiscal year, so a number without the year would repeat. A token format needs
///         <c>{counter}</c>.</item>
///         <item><c>StartValue</c> (AB#9755, D5) is at least 1 and not above <c>End</c>. It is a floor, never a reset: a
///         start value below a counter already in use is valid and ignored by the allocation, so it is not refused.</item>
///         <item>A legacy format and a range without the new attributes are taken as they are: they number as before.</item>
///     </list>
/// </remarks>
public sealed class AutoIncrementRangeSettings
{
    internal const string FormatAttribute = "Format";
    internal const string StartValueAttribute = "StartValue";
    internal const string EndAttribute = "End";
    internal const string CurrentValueAttribute = "CurrentValue";
    internal const string PeriodKindAttribute = "PeriodKind";
    internal const string FiscalYearStartMonthAttribute = "FiscalYearStartMonth";
    internal const string ReferenceTimeZoneAttribute = "ReferenceTimeZone";

    /// <summary>Value of <c>AutoIncrementPeriodKinds.FiscalYear</c>.</summary>
    internal const int PeriodKindFiscalYear = 1;

    private AutoIncrementRangeSettings(AutoIncrementNumberFormat format, bool hasFiscalYearPeriods,
        int fiscalYearStartMonth, TimeZoneInfo? referenceTimeZone, long? startValue)
    {
        Format = format;
        HasFiscalYearPeriods = hasFiscalYearPeriods;
        FiscalYearStartMonth = fiscalYearStartMonth;
        ReferenceTimeZone = referenceTimeZone;
        StartValue = startValue;
    }

    /// <summary>The parsed number format.</summary>
    public AutoIncrementNumberFormat Format { get; }

    /// <summary>True for <c>PeriodKind = FiscalYear</c>: one counter per fiscal year.</summary>
    public bool HasFiscalYearPeriods { get; }

    /// <summary>Month 1-12 in which the fiscal year starts.</summary>
    public int FiscalYearStartMonth { get; }

    /// <summary>The zone in which a time is read to decide the fiscal year; set whenever there are periods.</summary>
    public TimeZoneInfo? ReferenceTimeZone { get; }

    /// <summary>The configured <c>StartValue</c>, if any.</summary>
    public long? StartValue { get; }

    /// <summary>The first number of a new fiscal year: <c>StartValue</c>, or 1.</summary>
    public long FirstNumberOfAPeriod => StartValue ?? 1;

    /// <summary>
    ///     The fiscal year of <paramref name="instant" />: the time is read in <see cref="ReferenceTimeZone" />, and a
    ///     fiscal year is named by the calendar year in which it starts (D4: start month 7, 30 June 2028 is in 2027).
    /// </summary>
    public int FiscalYearOf(DateTimeOffset instant)
    {
        var zone = ReferenceTimeZone ??
                   throw new InvalidOperationException("A range without fiscal-year periods has no fiscal year.");
        var local = TimeZoneInfo.ConvertTime(instant, zone);
        return local.Month >= FiscalYearStartMonth ? local.Year : local.Year - 1;
    }

    /// <summary>
    ///     A range already in use without fiscal-year periods (<c>CurrentValue</c> n &gt; 0) that is switched to
    ///     <c>PeriodKind = FiscalYear</c> would start the current fiscal year at <c>StartValue</c> (or 1) and issue numbers
    ///     it already issued (e.g. <c>26-{0:D4}</c> becoming <c>{yy}-{counter:D4}</c>). The switch is allowed only with
    ///     a <c>StartValue</c> above n. Returns the reason when <paramref name="next" /> must be refused, else null.
    /// </summary>
    internal static string? CheckSwitchToFiscalYear(RtEntity stored, RtEntity next)
    {
        if (IsFiscalYear(stored) || !IsFiscalYear(next))
        {
            return null;
        }

        if (!TryGetLong(stored, CurrentValueAttribute, out var currentValue, out _) || currentValue is null or <= 0)
        {
            return null;
        }

        TryGetLong(next, StartValueAttribute, out var startValue, out _);
        if (startValue > currentValue)
        {
            return null;
        }

        return $"the range is in use without fiscal-year periods (CurrentValue {currentValue}); switching it to " +
               $"PeriodKind FiscalYear would issue numbers from {startValue ?? 1} on again. Set StartValue above " +
               $"{currentValue}, or configure a new range.";
    }

    private static bool IsFiscalYear(RtEntity counter) =>
        TryGetLong(counter, PeriodKindAttribute, out var periodKind, out _) && periodKind == PeriodKindFiscalYear;

    /// <summary>Reads and checks the range configuration of the counter entity <paramref name="counter" />.</summary>
    public static bool TryCreate(RtEntity counter, out AutoIncrementRangeSettings settings, out string? error)
    {
        settings = null!;

        var formatText = counter.GetAttributeValueOrDefault(FormatAttribute) as string;
        if (!AutoIncrementNumberFormat.TryParse(formatText, out var format, out var formatError))
        {
            error = $"Format '{formatText}' is not valid: {formatError}.";
            return false;
        }

        if (!TryGetLong(counter, PeriodKindAttribute, out var periodKind, out error) ||
            !TryGetLong(counter, FiscalYearStartMonthAttribute, out var startMonth, out error) ||
            !TryGetLong(counter, StartValueAttribute, out var startValue, out error) ||
            !TryGetLong(counter, EndAttribute, out var end, out error))
        {
            return false;
        }

        if (startValue < 1)
        {
            error = $"StartValue {startValue} is below 1.";
            return false;
        }

        if (startValue > end)
        {
            error = $"StartValue {startValue} is above End {end}.";
            return false;
        }

        if (periodKind is not (null or 0 or PeriodKindFiscalYear))
        {
            error = $"PeriodKind {periodKind} is not known (None = 0, FiscalYear = 1).";
            return false;
        }

        if (startMonth is < 1 or > 12)
        {
            error = $"FiscalYearStartMonth {startMonth} is not a month (1-12).";
            return false;
        }

        var hasPeriods = periodKind == PeriodKindFiscalYear;
        TimeZoneInfo? zone = null;
        var zoneId = counter.GetAttributeValueOrDefault(ReferenceTimeZoneAttribute) as string;
        if (hasPeriods)
        {
            if (string.IsNullOrWhiteSpace(zoneId))
            {
                error = "ReferenceTimeZone is required when PeriodKind is FiscalYear (an IANA id such as " +
                        "'Europe/Vienna'); there is no UTC default.";
                return false;
            }

            if (!TryFindIanaTimeZone(zoneId, out zone))
            {
                error = $"ReferenceTimeZone '{zoneId}' is not a known IANA time zone id.";
                return false;
            }
        }
        else if (!string.IsNullOrWhiteSpace(zoneId) && !TryFindIanaTimeZone(zoneId, out _))
        {
            // Not needed without periods, but a configured zone must still be a real one.
            error = $"ReferenceTimeZone '{zoneId}' is not a known IANA time zone id.";
            return false;
        }

        if (format.UsesFiscalYear && !hasPeriods)
        {
            error = $"Format '{formatText}' shows the fiscal year ({{yyyy}} or {{yy}}), which needs PeriodKind " +
                    "FiscalYear.";
            return false;
        }

        // The counter restarts every fiscal year, so a number that does not show the year repeats every year:
        // a duplicate (AB#8675). A FiscalYear range therefore needs a token format with {yyyy} or {yy}.
        if (hasPeriods && !format.UsesFiscalYear)
        {
            var shown = string.IsNullOrEmpty(formatText) ? "no Format" : $"Format '{formatText}'";
            error = $"PeriodKind FiscalYear needs a Format that shows the fiscal year ({{yyyy}} or {{yy}}, e.g. " +
                    $"RE-{{yyyy}}-{{counter:D5}}); with {shown} the numbers would repeat every fiscal year.";
            return false;
        }

        settings = new AutoIncrementRangeSettings(format, hasPeriods, (int)(startMonth ?? 1), zone, startValue);
        error = null;
        return true;
    }

    /// <summary>
    ///     An IANA id the runtime knows. A Windows id (e.g. 'W. Europe Standard Time') is refused although .NET would
    ///     convert it: the attribute is defined as an IANA id, and only that is portable across hosts.
    /// </summary>
    internal static bool TryFindIanaTimeZone(string id, out TimeZoneInfo? zone)
    {
        zone = null;
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(id, out var found))
        {
            return false;
        }

        if (!found.HasIanaId && !TimeZoneInfo.TryConvertIanaIdToWindowsId(id, out _))
        {
            return false;
        }

        zone = found;
        return true;
    }

    private static bool TryGetLong(RtEntity counter, string attributeName, out long? value, out string? error)
    {
        error = null;
        value = null;
        var raw = counter.GetAttributeValueOrDefault(attributeName);
        switch (raw)
        {
            case null:
                return true;
            case string:
                error = $"{attributeName} '{raw}' is not a number.";
                return false;
            case Enum e:
                value = Convert.ToInt64(e, CultureInfo.InvariantCulture);
                return true;
            case IConvertible convertible:
                try
                {
                    value = convertible.ToInt64(CultureInfo.InvariantCulture);
                    return true;
                }
                catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
                {
                    error = $"{attributeName} '{raw}' is not a number.";
                    return false;
                }
            default:
                error = $"{attributeName} '{raw}' is not a number.";
                return false;
        }
    }
}
