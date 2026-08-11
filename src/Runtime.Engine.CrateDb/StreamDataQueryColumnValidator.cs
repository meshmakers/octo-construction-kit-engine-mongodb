using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;

// Two types share the simple name StreamDataException: the connection-level one local to this
// namespace, and Runtime.Contracts.StreamData's, which is the documented base for stream-data and
// archive failures (concept §12). Inside this namespace the local type would win the unqualified
// lookup, so the contracts type is aliased explicitly.
//
// The reason is catchability, not the HTTP status. These same query methods already throw
// ArchiveNotFoundException and ArchiveNotActivatedException, both derived from the contracts base, so
// a caller who catches that base catches this rejection too; the local type shares nothing with them.
// (No consumer distinguishes the two today — GraphQL surfaces any exception alike with
// ExposeExceptionDetails, and StreamDataController exposes no query endpoint at all — so this is about
// giving callers one clause to catch, not about the response code.)
using ContractsStreamDataException = Meshmakers.Octo.Runtime.Contracts.StreamData.StreamDataException;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// Rejects a stream data query that names a column the field resolver cannot resolve (AB#4765).
/// <para>
/// Before this guard existed every query path dropped an unresolvable name without raising anything,
/// and the damage depended on where the name sat: a mistyped sort returned rows in storage order, a
/// mistyped filter returned <em>more</em> rows than asked for, a dropped group-by column merged
/// groups that should have stayed apart, and a dropped aggregation left its key figure missing
/// altogether. Every one of those looks like a plausible result, which is what made it dangerous —
/// nothing in the response said a criterion had been ignored.
/// </para>
/// <para>
/// Validation runs before any SQL is built, matching the repository's "pre-validate, then commit,
/// never partial" convention (see <c>InsertTimeRangeAsync</c>), and mirrors what the runtime-data
/// path has done for a long time: collect every offending name, then throw once naming them all
/// (<c>RtTransientQuery</c> / <c>RtMutationBase</c> in the asset repository services).
/// </para>
/// </summary>
internal static class StreamDataQueryColumnValidator
{
    /// <summary>
    /// Stands in for a null, empty or whitespace-only column name in the error message. Such a name is
    /// more reachable than it looks: the contract types validate their attribute path with
    /// <c>ArgumentValidation.ValidateString</c>, which only rejects null and empty, so a
    /// whitespace-only path passes; and the projection / group-by lists are plain strings with no
    /// validation at all. Parentheses cannot occur in a CK attribute path or a physical column name, so
    /// this can never be mistaken for a real name.
    /// </summary>
    private const string BlankNamePlaceholder = "(blank)";

    /// <summary>
    /// Validates every column name a query carries. Pass the lists that apply to the query kind;
    /// the ones it does not have are simply null.
    /// </summary>
    /// <param name="resolver">The resolver built for the queried archive.</param>
    /// <param name="archiveRtId">The queried archive, carried on the exception for context.</param>
    /// <param name="columns">Projected column names.</param>
    /// <param name="sortOrders">Sort criteria.</param>
    /// <param name="fieldFilters">Field filters.</param>
    /// <param name="groupByColumns">Group-by column names.</param>
    /// <param name="aggregationColumns">Aggregation columns.</param>
    /// <exception cref="ContractsStreamDataException">
    /// Thrown when at least one column name cannot be resolved.
    /// </exception>
    /// <remarks>
    /// A filter that will be dropped downstream anyway is skipped entirely — neither its value nor its
    /// column name is checked. <c>BuildFieldFilterDtos</c> discards a filter whose operator needs a
    /// comparison value but carries none before it ever resolves the name, so such a filter cannot
    /// affect the result and there is nothing to protect against. The GraphQL layer draws the same line
    /// (<c>fieldFilters?.Where(f =&gt; f.ComparisonValue != null)</c>) yet still forwards the full list,
    /// so validating those names here would reject exactly the half-filled filter rows that the query
    /// builder deliberately tolerates.
    /// <para>
    /// <c>IsNull</c> / <c>IsNotNull</c> are the exception: they legitimately carry no value but do take
    /// effect, so their column names are validated like any other. A blank attribute path cannot occur
    /// on a filter at all — <see cref="FieldFilter" /> rejects one in its constructor.
    /// </para>
    /// </remarks>
    internal static void Validate(
        StreamDataFieldResolver resolver,
        OctoObjectId archiveRtId,
        IReadOnlyList<string>? columns = null,
        IReadOnlyList<SortOrderItem>? sortOrders = null,
        IReadOnlyList<FieldFilter>? fieldFilters = null,
        IReadOnlyList<string>? groupByColumns = null,
        IReadOnlyList<AggregationColumn>? aggregationColumns = null)
    {
        // Order is the message order: what the query selects, then how it is shaped, then how it is
        // narrowed — the order an author reads their own query in.
        var offenders = new List<(string Usage, IReadOnlyList<string> Names)>
        {
            ("projection", Unresolvable(resolver, columns)),
            ("aggregation", Unresolvable(resolver, aggregationColumns?.Select(c => c.AttributePath))),
            ("grouping", Unresolvable(resolver, groupByColumns)),
            ("sorting", Unresolvable(resolver, sortOrders?.Select(s => s.AttributePath))),
            ("field filter", Unresolvable(resolver, fieldFilters?.Where(TakesEffect)
                .Select(f => f.AttributePath)))
        };

        if (offenders.Any(o => o.Names.Count > 0))
        {
            throw UnknownColumns(offenders, resolver.KnownFieldNames, archiveRtId);
        }
    }

    /// <summary>
    /// Whether a filter will reach the query at all. <c>BuildFieldFilterDtos</c> skips one that needs a
    /// comparison value and carries none, so its name is never resolved and a bad name there changes
    /// nothing. Validating it would only reject half-filled rows from the query builder.
    /// </summary>
    private static bool TakesEffect(FieldFilter filter)
        => filter.ComparisonValue != null
           || filter.Operator is FieldFilterOperator.IsNull or FieldFilterOperator.IsNotNull;

    /// <summary>
    /// Every offending name in one message, grouped by where it was used, together with the names that
    /// would have worked — a caller who mistyped three columns should learn all three at once rather
    /// than one per round trip. The hint about physical spelling is there because the standard fields
    /// are the ones callers get wrong: the lookup is case-insensitive but not separator-insensitive,
    /// so <c>WindowStart</c> misses <c>window_start</c>.
    /// </summary>
    private static Exception UnknownColumns(
        IReadOnlyList<(string Usage, IReadOnlyList<string> Names)> offendersByUsage,
        IEnumerable<string> knownColumns,
        OctoObjectId archiveRtId)
    {
        var parts = offendersByUsage
            .Where(g => g.Names.Count > 0)
            .Select(g => $"{g.Usage}: {string.Join(", ", g.Names.Select(n => $"'{n}'"))}");

        var valid = string.Join(", ", knownColumns.OrderBy(n => n, StringComparer.OrdinalIgnoreCase));

        return new ContractsStreamDataException(
            $"Stream data query on archive '{archiveRtId}' references unknown columns "
            + $"({string.Join("; ", parts)}). Valid columns are: {valid}. Names are matched "
            + "case-insensitively against the archive's columns and the standard fields; note that "
            + "the standard fields use their physical spelling (e.g. 'window_start', not 'WindowStart').",
            archiveRtId);
    }

    /// <summary>
    /// The names the resolver rejects, de-duplicated case-insensitively so the same typo repeated
    /// across a query is reported once. A null or blank name counts as unresolvable — it can never
    /// address a column, and passing it to the resolver's dictionary would throw.
    /// </summary>
    private static IReadOnlyList<string> Unresolvable(
        StreamDataFieldResolver resolver,
        IEnumerable<string>? names)
    {
        if (names == null)
        {
            return [];
        }

        return names
            .Where(name => string.IsNullOrWhiteSpace(name) || resolver.Resolve(name) == null)
            // A blank name has no spelling worth echoing, and quoting it verbatim puts either '' or a
            // run of invisible spaces in the message — the one place the caller looks to find out what
            // went wrong. Normalising also collapses "" and "  " into a single report entry instead of
            // two, since the comparer below treats them as different names.
            .Select(name => string.IsNullOrWhiteSpace(name) ? BlankNamePlaceholder : name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
