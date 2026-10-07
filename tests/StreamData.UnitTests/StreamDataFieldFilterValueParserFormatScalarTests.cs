using System.Globalization;
using Meshmakers.Octo.Runtime.Engine.CrateDb.QueryBuilder;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb.UnitTests;

/// <summary>
/// A field-filter comparison value is rendered to text and handed to CrateDB, so the rendering
/// must not depend on the host's culture. It used to: an ISO timestamp arriving through a
/// pipeline is boxed as a <see cref="DateTime" /> (JsonScalar.ToClr parses date strings for
/// Newtonsoft parity), and <c>ToString()</c> on an en-US host produced
/// <c>09/30/2025 22:00:00</c>, which CrateDB rejects with
/// "Cannot cast ... of type `text` to type `timestamp with time zone`". A German host is worse
/// for numbers: <c>0,08</c> parses, with a different meaning.
/// </summary>
public class StreamDataFieldFilterValueParserFormatScalarTests
{
    private static T WithCulture<T>(string culture, Func<T> body)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo(culture);
        try
        {
            return body();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-AT")]
    [InlineData("")]
    public void DateTime_RendersRoundTrippable_RegardlessOfCulture(string culture)
    {
        var value = new DateTime(2025, 9, 30, 22, 0, 0, DateTimeKind.Utc);

        var rendered = WithCulture(culture, () => StreamDataFieldFilterValueParser.FormatScalar(value));

        Assert.Equal("2025-09-30T22:00:00.0000000Z", rendered);
        Assert.Equal(value, DateTime.Parse(rendered, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-AT")]
    public void Double_UsesInvariantDecimalSeparator(string culture)
    {
        var rendered = WithCulture(culture, () => StreamDataFieldFilterValueParser.FormatScalar(0.08d));

        Assert.Equal("0.08", rendered);
    }

    [Fact]
    public void DateTimeOffset_KeepsItsOffset()
    {
        var value = new DateTimeOffset(2025, 9, 30, 22, 0, 0, TimeSpan.FromHours(2));

        var rendered = WithCulture("en-US", () => StreamDataFieldFilterValueParser.FormatScalar(value));

        Assert.Equal("2025-09-30T22:00:00.0000000+02:00", rendered);
    }

    [Fact]
    public void String_PassesThroughUnchanged()
    {
        Assert.Equal("1-1:2.9.0 G.03", StreamDataFieldFilterValueParser.FormatScalar("1-1:2.9.0 G.03"));
    }

    [Fact]
    public void Null_RendersEmpty()
    {
        Assert.Equal(string.Empty, StreamDataFieldFilterValueParser.FormatScalar(null));
    }

    [Fact]
    public void ParseInValues_RendersEachElementInvariantly()
    {
        var values = new object[] { new DateTime(2025, 9, 30, 22, 0, 0, DateTimeKind.Utc), 0.08d };

        var parsed = WithCulture("de-AT", () => StreamDataFieldFilterValueParser.ParseInValues(values));

        Assert.Equal(["2025-09-30T22:00:00.0000000Z", "0.08"], parsed);
    }
}
