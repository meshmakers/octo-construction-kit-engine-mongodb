using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.PreDocumentModifications;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests.AutoIncrement;

/// <summary>
///     The reader of the gap report without a database: a number is read back into its counter value only when it is a
///     text the format writes for that fiscal year, and the prefix is the literal text before the counter (the fiscal
///     year included), which the report uses as the anchored index range of the number attribute.
/// </summary>
public class AutoIncrementNumberReaderTests
{
    private static AutoIncrementNumberReader Reader(string? format, int? fiscalYear)
    {
        Assert.True(AutoIncrementNumberFormat.TryParse(format, out var parsed, out var error), error);
        return parsed.CreateReader(fiscalYear);
    }

    [Theory]
    [InlineData("RE-{yyyy}-{counter:D5}", 2027, "RE-2027-")]
    [InlineData("{yy}/{counter:D3}", 2027, "27/")]
    [InlineData("GS {counter:D2} ({yyyy})", 2026, "GS ")]
    [InlineData("INV-{0:D6}", null, "INV-")]
    [InlineData("{{x}}-{0:D4}", null, "{x}-")]
    [InlineData("{0:D4}-A", null, "")]
    [InlineData(null, null, "")]
    public void Prefix_IsTheTextBeforeTheCounter(string? format, int? fiscalYear, string expected)
    {
        Assert.Equal(expected, Reader(format, fiscalYear).Prefix);
    }

    [Theory]
    [InlineData("RE-{yyyy}-{counter:D5}", 2027, "RE-2027-00007", 7)]
    [InlineData("RE-{yyyy}-{counter:D5}", 2027, "RE-2027-123456", 123456)]
    [InlineData("{yy}/{counter:D3}", 2027, "27/009", 9)]
    [InlineData("GS {counter:D2} ({yyyy})", 2026, "GS 03 (2026)", 3)]
    [InlineData("INV-{0:D6}", null, "INV-000042", 42)]
    [InlineData("{{x}}-{0:D4}", null, "{x}-0042", 42)]
    [InlineData(null, null, "15", 15)]
    public void TryRead_ReadsWhatTheFormatWrites(string? format, int? fiscalYear, string number, long expected)
    {
        var reader = Reader(format, fiscalYear);

        Assert.True(reader.TryRead(number, out var value));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("RE-{yyyy}-{counter:D5}", 2027, "RE-2026-00007")] // another fiscal year
    [InlineData("RE-{yyyy}-{counter:D5}", 2027, "RE-2027-0007")] // other padding
    [InlineData("RE-{yyyy}-{counter:D5}", 2027, "RE-2027-000007")] // other padding
    [InlineData("RE-{yyyy}-{counter:D5}", 2027, "GS-2027-00007")] // another prefix
    [InlineData("RE-{yyyy}-{counter:D5}", 2027, "RE-2027-00007x")] // trailing text
    [InlineData("RE-{yyyy}-{counter:D5}", 2027, "")]
    [InlineData("INV-{0:D6}", null, "INV-42")]
    [InlineData(null, null, "007")]
    [InlineData(null, null, "-1")]
    public void TryRead_RefusesWhatTheFormatDoesNotWrite(string? format, int? fiscalYear, string number)
    {
        Assert.False(Reader(format, fiscalYear).TryRead(number, out _));
    }
}
