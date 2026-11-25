using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.PreDocumentModifications;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.AutoIncrement;

/// <summary>
///     AB#9754 without a database: the token format of a number range (<c>{yyyy}</c>, <c>{yy}</c>,
///     <c>{counter:D&lt;n&gt;}</c>), the legacy <c>string.Format</c> format, the fiscal year of a time and the rules a
///     range configuration must meet (above all: a fiscal-year range needs a known IANA zone, no UTC default).
/// </summary>
public class AutoIncrementNumberFormatTests
{
    private static readonly RtCkId<CkTypeId> AutoIncrementTypeId = new("System/AutoIncrement");

    [Theory]
    [InlineData("{0:D4}", 42, "0042")]
    [InlineData("INV-{0:D6}", 7, "INV-000007")]
    [InlineData("26-{0:D4}", 1234, "26-1234")]
    [InlineData("{0}", 5, "5")]
    public void LegacyFormat_IsStringFormat_ExactlyAsBefore(string format, long value, string expected)
    {
        Assert.True(AutoIncrementNumberFormat.TryParse(format, out var parsed, out _));

        Assert.False(parsed.IsTokenFormat);
        Assert.Equal(string.Format(format, value), parsed.Format(value, null));
        Assert.Equal(expected, parsed.Format(value, null));
    }

    [Theory]
    [InlineData("RE-{yyyy}-{counter:D5}", 1, 2027, "RE-2027-00001")]
    [InlineData("RE-{yyyy}-{counter:D5}", 123456, 2026, "RE-2026-123456")]
    [InlineData("{yy}/{counter:D3}", 9, 2027, "27/009")]
    [InlineData("{yy}/{counter}", 9, 2005, "05/9")]
    [InlineData("GS {counter:D2} ({yyyy})", 3, 2026, "GS 03 (2026)")]
    public void TokenFormat_ShowsFiscalYearAndCounter(string format, long value, int fiscalYear, string expected)
    {
        Assert.True(AutoIncrementNumberFormat.TryParse(format, out var parsed, out var error), error);

        Assert.True(parsed.IsTokenFormat);
        Assert.Equal(expected, parsed.Format(value, fiscalYear));
    }

    [Fact]
    public void TokenFormat_WithoutYear_NeedsNoFiscalYear()
    {
        Assert.True(AutoIncrementNumberFormat.TryParse("K-{counter:D4}", out var parsed, out _));

        Assert.False(parsed.UsesFiscalYear);
        Assert.Equal("K-0042", parsed.Format(42, null));
    }

    /// <summary>
    ///     AB#9755: a legacy format that string.Format cannot format is refused when it is parsed, not at the first
    ///     allocation.
    /// </summary>
    [Theory]
    [InlineData("X-{counter:Dx}")]
    [InlineData("X-{1}")]
    [InlineData("X-{0")]
    [InlineData("X-{0:D4}}")]
    public void LegacyFormat_ThatStringFormatCannotFormat_IsRefused(string format)
    {
        Assert.False(AutoIncrementNumberFormat.TryParse(format, out _, out var error));
        Assert.Contains("string.Format cannot format it", error);
    }

    [Theory]
    [InlineData("RE-{yyyy}-{0:D4}")]
    [InlineData("RE-{yyyy}-{counter:X5}")]
    [InlineData("RE-{yyyy}-{counter:D0}")]
    [InlineData("RE-{yyyy}-{counter:D19}")]
    [InlineData("RE-{yyyy-{counter}")]
    [InlineData("RE-{yyyy}}-{counter}")]
    public void TokenFormat_WithAnUnknownOrBrokenPlaceholder_IsRefused(string format)
    {
        Assert.False(AutoIncrementNumberFormat.TryParse(format, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Theory]
    // Fiscal year = calendar year (start month 1), Vienna: 00:30 local on 1 January is already the new year
    // although it is still 31 December in UTC - the zone decides, not UTC.
    [InlineData(1, "2026-12-31T23:30:00+01:00", 2026)]
    [InlineData(1, "2027-01-01T00:30:00+01:00", 2027)]
    // Start month 7: a fiscal year is named by the calendar year in which it starts (D4).
    [InlineData(7, "2027-06-30T23:59:00+02:00", 2026)]
    [InlineData(7, "2027-07-01T00:00:00+02:00", 2027)]
    [InlineData(7, "2028-06-30T12:00:00+02:00", 2027)]
    public void FiscalYear_IsReadInTheReferenceTimeZone(int startMonth, string instant, int expected)
    {
        var settings = Settings(periodKind: 1, zone: "Europe/Vienna", startMonth: startMonth);

        Assert.Equal(expected, settings.FiscalYearOf(DateTimeOffset.Parse(instant)));
    }

    [Fact]
    public void FiscalYearRange_WithoutAZone_IsRefused_ThereIsNoUtcDefault()
    {
        Assert.False(AutoIncrementRangeSettings.TryCreate(Counter(periodKind: 1), out _, out var error));
        Assert.Contains("ReferenceTimeZone is required", error);
    }

    [Theory]
    [InlineData("Mars/Olympus_Mons")]
    [InlineData("W. Europe Standard Time")]
    [InlineData("   ")]
    public void FiscalYearRange_WithAnUnknownOrNonIanaZone_IsRefused(string zone)
    {
        Assert.False(AutoIncrementRangeSettings.TryCreate(Counter(periodKind: 1, zone: zone), out _, out var error));
        Assert.Contains("ReferenceTimeZone", error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(13)]
    public void FiscalYearStartMonth_OutsideOneToTwelve_IsRefused(int startMonth)
    {
        Assert.False(AutoIncrementRangeSettings.TryCreate(
            Counter(periodKind: 1, zone: "Europe/Vienna", startMonth: startMonth), out _, out var error));
        Assert.Contains("FiscalYearStartMonth", error);
    }

    /// <summary>
    ///     A legacy format with string.Format-escaped braces around token text stays a legacy format and renders as
    ///     before; an unescaped token still selects the token syntax.
    /// </summary>
    [Theory]
    [InlineData("{{yyyy}}-{0:D4}", "{yyyy}-0042")]
    [InlineData("{{counter}}/{0}", "{counter}/42")]
    [InlineData("{{{{yy}}}}-{0:D4}", "{{yy}}-0042")]
    public void EscapedTokenText_StaysALegacyFormat(string format, string expected)
    {
        Assert.False(AutoIncrementNumberFormat.IsTokenFormatString(format));
        Assert.True(AutoIncrementNumberFormat.TryParse(format, out var result, out var error), error);
        Assert.False(result.IsTokenFormat);
        Assert.Equal(expected, result.Format(42, null));
    }

    [Theory]
    [InlineData("RE-{yyyy}-{counter:D5}")]
    [InlineData("{counter}")]
    [InlineData("{{{yy}-{counter}")]
    public void UnescapedToken_SelectsTheTokenSyntax(string format)
    {
        Assert.True(AutoIncrementNumberFormat.IsTokenFormatString(format));
    }

    [Theory]
    [InlineData("RE-{yyyy}")]
    [InlineData("{yy}")]
    [InlineData("RE-{yyyy}-{yy}")]
    public void TokenFormat_WithoutTheCounter_IsRefused(string format)
    {
        Assert.False(AutoIncrementNumberFormat.TryParse(format, out _, out var error));
        Assert.Contains("needs the counter", error);
    }

    /// <summary>
    ///     AB#8675: the counter of a FiscalYear range restarts every year, so a number that does not show the year
    ///     would repeat: no format, a legacy format and a token format without {yyyy}/{yy} are refused.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("{0:D4}")]
    [InlineData("INV-{0}")]
    [InlineData("RE-{counter:D5}")]
    public void FiscalYearRange_WhoseFormatDoesNotShowTheYear_IsRefused(string? format)
    {
        Assert.False(AutoIncrementRangeSettings.TryCreate(
            Counter(periodKind: 1, zone: "Europe/Vienna", format: format), out _, out var error));
        Assert.Contains("needs a Format that shows the fiscal year", error);
    }

    [Fact]
    public void YearToken_WithoutFiscalYearPeriods_IsRefused()
    {
        Assert.False(AutoIncrementRangeSettings.TryCreate(Counter(format: "RE-{yyyy}-{counter:D5}"), out _,
            out var error));
        Assert.Contains("needs PeriodKind FiscalYear", error);
    }

    [Fact]
    public void LegacyCounter_WithoutTheNewAttributes_IsValid_AndHasNoPeriods()
    {
        Assert.True(AutoIncrementRangeSettings.TryCreate(Counter(format: "{0:D4}"), out var settings, out var error),
            error);

        Assert.False(settings.HasFiscalYearPeriods);
        Assert.Null(settings.ReferenceTimeZone);
        Assert.Equal("0042", settings.Format.Format(42, null));
    }

    [Fact]
    public void FiscalYearRange_StartsANewPeriodAtStartValue_OrOne()
    {
        Assert.Equal(1, Settings(periodKind: 1, zone: "Europe/Vienna").FirstNumberOfAPeriod);
        Assert.Equal(100, Settings(periodKind: 1, zone: "Europe/Vienna", startValue: 100).FirstNumberOfAPeriod);
    }

    private static AutoIncrementRangeSettings Settings(int? periodKind = null, string? zone = null,
        int? startMonth = null, string? format = null, int? startValue = null)
    {
        // A FiscalYear range needs a format that shows the year.
        format ??= periodKind == 1 ? "RE-{yyyy}-{counter:D5}" : null;
        Assert.True(AutoIncrementRangeSettings.TryCreate(Counter(periodKind, zone, startMonth, format, startValue),
            out var settings, out var error), error);
        return settings;
    }

    private static RtEntity Counter(int? periodKind = null, string? zone = null, int? startMonth = null,
        string? format = null, int? startValue = null)
    {
        var attributes = new Dictionary<string, object?> { { "End", 1000L }, { "CurrentValue", 0L } };
        if (periodKind != null)
        {
            attributes["PeriodKind"] = (long)periodKind.Value;
        }

        if (zone != null)
        {
            attributes["ReferenceTimeZone"] = zone;
        }

        if (startMonth != null)
        {
            attributes["FiscalYearStartMonth"] = (long)startMonth.Value;
        }

        if (format != null)
        {
            attributes["Format"] = format;
        }

        if (startValue != null)
        {
            attributes["StartValue"] = (long)startValue.Value;
        }

        return new RtEntity(AutoIncrementTypeId, OctoObjectId.GenerateNewId(), attributes);
    }
}
