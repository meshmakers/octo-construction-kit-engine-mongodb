using System.Text.RegularExpressions;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
// Same collision the validator itself has to disambiguate: the contracts type is the one the
// controller catches, so it is the one the validator must throw.
using StreamDataException = Meshmakers.Octo.Runtime.Contracts.StreamData.StreamDataException;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// AB#4765: a column name the resolver cannot resolve must be rejected, never dropped. Each position
/// a name can occupy had its own failure mode — a sort came back in storage order, a filter came back
/// wider than requested, a group-by merged groups, an aggregation lost its key figure — and all four
/// looked like plausible results.
/// </summary>
public class StreamDataQueryColumnValidatorTests
{
    private static readonly OctoObjectId Archive = OctoObjectId.GenerateNewId();

    private static StreamDataFieldResolver Resolver() =>
        new(["Temperature", "Amount.Value"], usesWindowedStorage: true);

    private static FieldFilter Filter(string path) =>
        new(path, FieldFilterOperator.Equals, "42");

    private static AggregationColumn Agg(string path) =>
        new(path, AggregationFunction.Sum);

    private static SortOrderItem Sort(string path) => new(path, SortOrders.Ascending);

    [Fact]
    public void ValidNames_DoNotThrow()
    {
        // The full accepted vocabulary: declared attributes (dotted and plain), the windowed defaults
        // in their physical spelling, and the identity columns.
        StreamDataQueryColumnValidator.Validate(Resolver(), Archive,
            columns: ["Temperature", "Amount.Value", "window_start", "rtWellKnownName"],
            sortOrders: [Sort("window_end")],
            fieldFilters: [Filter("rtId")],
            groupByColumns: ["rtId"],
            aggregationColumns: [Agg("Temperature")]);
    }

    [Fact]
    public void ComputedColumn_ResolvesUnderItsLogicalName()
    {
        // A computed column's physical name is versioned after a formula change, so it is registered
        // rather than derived (AB#4764). Validation must accept the logical name it was registered
        // under, or projecting a formula column would become impossible.
        var resolver = Resolver();
        resolver.RegisterComputedColumn("power", "power__v3");

        StreamDataQueryColumnValidator.Validate(resolver, Archive,
            columns: ["power"],
            aggregationColumns: [Agg("power")]);
    }

    [Theory]
    [InlineData("projection")]
    [InlineData("aggregation")]
    [InlineData("grouping")]
    [InlineData("sorting")]
    [InlineData("field filter")]
    public void UnknownName_ThrowsNamingTheUsage(string usage)
    {
        const string bad = "Temparatur";

        var act = () => StreamDataQueryColumnValidator.Validate(Resolver(), Archive,
            columns: usage == "projection" ? [bad] : null,
            sortOrders: usage == "sorting" ? [Sort(bad)] : null,
            fieldFilters: usage == "field filter" ? [Filter(bad)] : null,
            groupByColumns: usage == "grouping" ? [bad] : null,
            aggregationColumns: usage == "aggregation" ? [Agg(bad)] : null);

        var ex = Assert.Throws<StreamDataException>(act);
        Assert.Contains(usage, ex.Message);
        Assert.Contains(bad, ex.Message);
        // The message has to say what would have worked — the caller just guessed wrong once.
        Assert.Contains("Temperature", ex.Message);
    }

    [Fact]
    public void WindowStart_InResultHeaderSpelling_IsRejected()
    {
        // The exact case from the field report: the storage layer knows window_start, not WindowStart,
        // and the difference is a separator rather than casing — so the case-insensitive lookup misses.
        var ex = Assert.Throws<StreamDataException>(
            () => StreamDataQueryColumnValidator.Validate(Resolver(), Archive, sortOrders: [Sort("WindowStart")]));

        Assert.Contains("WindowStart", ex.Message);
        Assert.Contains("window_start", ex.Message);
    }

    [Fact]
    public void SeveralBadNames_AreAllReportedAtOnce()
    {
        // Three typos should cost one round trip, not three. Same reason RtTransientQuery collects
        // before throwing.
        var ex = Assert.Throws<StreamDataException>(
            () => StreamDataQueryColumnValidator.Validate(Resolver(), Archive,
                columns: ["Temparatur"],
                sortOrders: [Sort("WindowStart")],
                fieldFilters: [Filter("amount.valu")]));

        Assert.Contains("Temparatur", ex.Message);
        Assert.Contains("WindowStart", ex.Message);
        Assert.Contains("amount.valu", ex.Message);
    }

    [Fact]
    public void SameTypoTwice_IsReportedOnce()
    {
        var ex = Assert.Throws<StreamDataException>(
            () => StreamDataQueryColumnValidator.Validate(Resolver(), Archive,
                columns: ["Temparatur", "TEMPARATUR"]));

        // De-duplicated case-insensitively: one mistake, one mention.
        var occurrences = Regex.Matches(ex.Message, "Temparatur", RegexOptions.IgnoreCase).Count;
        Assert.Equal(1, occurrences);
    }

    [Theory]
    [InlineData("  ")]   // whitespace passes ArgumentValidation.ValidateString (IsNullOrEmpty only)
    [InlineData("")]     // reachable on the projection list, which is unvalidated plain strings
    public void BlankName_IsRejectedAndReportedAsAPlaceholder(string name)
    {
        // A blank attribute path cannot address a column. Quoting it verbatim would put '' or a run of
        // invisible spaces into the one message the caller reads to find the mistake.
        var ex = Assert.Throws<StreamDataException>(
            () => StreamDataQueryColumnValidator.Validate(Resolver(), Archive, columns: [name]));

        Assert.Contains("(blank)", ex.Message);
        Assert.DoesNotContain("''", ex.Message);
    }

    [Fact]
    public void SeveralBlankNames_CollapseToOneReportEntry()
    {
        // "" and "  " are different names to an ordinal comparer but the same mistake to a reader.
        var ex = Assert.Throws<StreamDataException>(
            () => StreamDataQueryColumnValidator.Validate(Resolver(), Archive, columns: ["", "  ", "\t"]));

        Assert.Single(Regex.Matches(ex.Message, Regex.Escape("(blank)")));
    }

    [Fact]
    public void RawArchiveResolver_RejectsWindowColumns()
    {
        // A raw archive has no row window, so naming one is a genuine mistake rather than a spelling
        // question — its time axis is `timestamp`.
        var raw = new StreamDataFieldResolver(["Temperature"], usesWindowedStorage: false);

        Assert.Throws<StreamDataException>(
            () => StreamDataQueryColumnValidator.Validate(raw, Archive, columns: ["window_start"]));

        StreamDataQueryColumnValidator.Validate(raw, Archive, columns: ["timestamp"]);
    }

    [Theory]
    [InlineData(FieldFilterOperator.Equals)]
    [InlineData(FieldFilterOperator.IsNull)]
    public void FilterValues_AreNotValidated(FieldFilterOperator op)
    {
        // A valid name passes regardless of whether the filter carries a comparison value.
        StreamDataQueryColumnValidator.Validate(Resolver(), Archive,
            fieldFilters: [new FieldFilter("Temperature", op, null)]);
    }

    [Fact]
    public void FilterWithoutComparisonValue_IsSkippedEntirely_EvenWithABadName()
    {
        // BuildFieldFilterDtos discards such a filter before it ever resolves the name, so a bad name
        // there cannot affect the result. Validating it would reject exactly the half-filled rows the
        // query builder tolerates and GraphQL forwards after excluding them from its own name check.
        StreamDataQueryColumnValidator.Validate(Resolver(), Archive,
            fieldFilters: [new FieldFilter("Temparatur", FieldFilterOperator.Equals, null)]);
    }

    [Theory]
    [InlineData(FieldFilterOperator.IsNull)]
    [InlineData(FieldFilterOperator.IsNotNull)]
    public void NullChecksCarryNoValueButTakeEffect_SoTheirNamesAreStillValidated(FieldFilterOperator op)
    {
        // The exception to the rule above: no comparison value, but the filter does reach the query.
        var ex = Assert.Throws<StreamDataException>(
            () => StreamDataQueryColumnValidator.Validate(Resolver(), Archive,
                fieldFilters: [new FieldFilter("Temparatur", op, null)]));

        Assert.Contains("field filter", ex.Message);
    }

    [Fact]
    public void NothingToValidate_DoesNotThrow()
    {
        StreamDataQueryColumnValidator.Validate(Resolver(), Archive);
    }
}
