using System.Collections;
using System.Globalization;

using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.DataPermissions;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Query;

using MongoDB.Bson;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.PreDocumentModifications;

/// <summary>
///     The gap report of one number range and one fiscal year (<see cref="ITenantRepository.GetNumberRangeGapReportAsync" />).
///     <list type="number">
///         <item>The range (<c>System/AutoIncrement</c> by its well-known name) is read with the caller's read filter;
///         its settings give the format, the start value and, per fiscal year, the counter: the interval is
///         <c>StartValue</c> (or 1) to the current value of the fiscal year's period (of the range without periods).</item>
///         <item>Every concrete CK type with an attribute whose <c>autoIncrementReference</c> names the range is read
///         with one aggregation per type and attribute: <c>$match</c> on the type, on the number attribute - for a String
///         number an anchored prefix (<see cref="AutoIncrementNumberReader.Prefix" />, the fiscal year included), so
///         the numbers of one fiscal year are a range of the index the document model declares on the number attribute,
///         for a numeric number every number -, on the released state values of the range's release configuration
///         (drafts have no number and are not read) and on the caller's data-permission read filter; <c>$project</c>
///         keeps the id and the number.</item>
///         <item>Each number is read back into its counter value with the range's format. Values seen are marked in a
///         bit set over the interval; a value seen twice is a duplicate, whose documents a second, indexed read
///         (<c>$in</c> on the duplicate numbers) collects. A number outside the interval, or one that does not have the
///         format, is irregular.</item>
///     </list>
///     The report never writes and never reads as the system: a document the caller may not read is not counted, so
///     its number shows as missing.
/// </summary>
internal sealed class NumberRangeGapReportQuery(
    string tenantId,
    ICkCacheService ckCacheService,
    IMongoDbRepositoryDataSource dataSource,
    Func<CkTypeGraph, Task<RtDataSecurityQueryFilter?>> readFilter)
{
    internal const string NumberField = "n";

    private const string AttributesField = "attributes";

    public async Task<NumberRangeGapReport?> RunAsync(IOctoSession session, string numberRange, int? fiscalYear,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(numberRange);
        var handle = ((IOctoSessionInternal)session).SessionHandle;

        var counter = await FindCounterAsync(handle, numberRange, cancellationToken).ConfigureAwait(false);
        if (counter == null)
        {
            return null;
        }

        if (!AutoIncrementRangeSettings.TryCreate(counter, out var settings, out var error) ||
            !NumberRangeRelease.TryCreate(counter, out var release, out error))
        {
            throw AutoIncrementFailedException.InvalidNumberRange(counter.RtId, numberRange, error!);
        }

        long currentValue;
        if (settings.HasFiscalYearPeriods)
        {
            if (fiscalYear == null)
            {
                throw new ArgumentException(
                    $"Number range '{numberRange}' restarts every fiscal year; the report needs the fiscal year.",
                    nameof(fiscalYear));
            }

            currentValue = AutoIncrementModifier.TryGetPeriodCurrentValue(counter, fiscalYear.Value) ??
                           settings.FirstNumberOfAPeriod - 1;
        }
        else
        {
            if (fiscalYear != null)
            {
                throw new ArgumentException(
                    $"Number range '{numberRange}' has no fiscal-year periods; the report takes no fiscal year.",
                    nameof(fiscalYear));
            }

            currentValue = Convert.ToInt64(counter.CurrentValue, CultureInfo.InvariantCulture);
        }

        var startValue = settings.FirstNumberOfAPeriod;
        var intervalLength = Math.Max(0, currentValue - startValue + 1);
        if (intervalLength > NumberRangeGapReportLimits.MaxInterval)
        {
            throw AutoIncrementFailedException.InvalidNumberRange(counter.RtId, numberRange,
                $"the interval {startValue}..{currentValue} is longer than the gap report checks " +
                $"({NumberRangeGapReportLimits.MaxInterval} numbers).");
        }

        var reader = settings.Format.CreateReader(fiscalYear);
        var seen = new BitArray((int)intervalLength);
        var duplicateValues = new SortedSet<long>();
        var irregular = new List<NumberRangeIrregularNumber>();
        long irregularCount = 0;
        long documentCount = 0;

        var sources = GetSources(numberRange, release);
        foreach (var source in sources)
        {
            var filter = await BuildFilterAsync(source, reader, null).ConfigureAwait(false);
            using var cursor = await source.Collection.AggregateAsync(handle, BuildPipeline(source, filter),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            while (await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
            {
                foreach (var row in cursor.Current)
                {
                    documentCount++;
                    var number = NumberText(row);
                    if (!TryReadValue(source, reader, row, out var value))
                    {
                        AddIrregular(new NumberRangeIrregularNumber(DocumentId(source, row), number, null));
                        continue;
                    }

                    if (value < startValue || value > currentValue)
                    {
                        AddIrregular(new NumberRangeIrregularNumber(DocumentId(source, row), number, value));
                        continue;
                    }

                    var index = (int)(value - startValue);
                    if (seen[index])
                    {
                        duplicateValues.Add(value);
                    }

                    seen[index] = true;
                }
            }
        }

        var duplicates = await CollectDuplicatesAsync(handle, sources, reader, settings, fiscalYear, duplicateValues,
            cancellationToken).ConfigureAwait(false);

        return new NumberRangeGapReport(numberRange, fiscalYear, startValue, currentValue, documentCount,
            MissingIntervals(seen, startValue, settings, fiscalYear), duplicates, irregular, irregularCount);

        void AddIrregular(NumberRangeIrregularNumber entry)
        {
            irregularCount++;
            if (irregular.Count < NumberRangeGapReportLimits.MaxIrregular)
            {
                irregular.Add(entry);
            }
        }
    }

    /// <summary>One CK type and one of its attributes numbered from the range.</summary>
    internal sealed record Source(
        CkTypeGraph TypeGraph,
        CkTypeAttributeGraph NumberAttribute,
        string NumberFieldPath,
        string? StateFieldPath,
        BsonArray? ReleasedStateValues,
        IMongoCollection<RtEntity> Collection);

    /// <summary>The concrete CK types and attributes numbered from <paramref name="numberRange" />.</summary>
    internal List<Source> GetSources(string numberRange, NumberRangeRelease? release)
    {
        var sources = new List<Source>();
        var types = ckCacheService.GetCkTypes(tenantId)
            .Where(t => !t.IsAbstract)
            .GroupBy(t => t.CkTypeId.ToRtCkId().FullName, StringComparer.Ordinal)
            .Select(g => g.First());
        foreach (var typeGraph in types)
        {
            foreach (var attribute in typeGraph.AllAttributes.Values.Where(a =>
                         string.Equals(a.AutoIncrementReference, numberRange, StringComparison.Ordinal)))
            {
                if (!typeGraph.AllAttributesByName.TryGetValue(attribute.AttributeName,
                        out CkTypeAttributeGraph? numberAttribute))
                {
                    continue;
                }

                string? stateField = null;
                BsonArray? stateValues = null;
                if (release != null)
                {
                    if (!typeGraph.AllAttributesByName.TryGetValue(release.StateAttribute,
                            out CkTypeAttributeGraph? stateAttribute))
                    {
                        // Without the state attribute a document of this type is never released, so never numbered.
                        continue;
                    }

                    stateField = AttributeField(stateAttribute.AttributeName);
                    stateValues = ReleasedStateValues(release, stateAttribute);
                }

                var collection = ((IMongoDbDataSourceCollection<OctoObjectId, RtEntity>)dataSource
                    .GetRtCollection<RtEntity>(typeGraph)).GetMongoCollection();
                sources.Add(new Source(typeGraph, numberAttribute, AttributeField(numberAttribute.AttributeName),
                    stateField, stateValues, collection));
            }
        }

        return sources;
    }

    /// <summary>
    ///     The <c>$match</c> of one source: the type, the number attribute (a String number by the anchored prefix of
    ///     the fiscal year, a numeric one by any number; <paramref name="numbers" /> narrows it to those values), the
    ///     released states and the caller's read filter.
    /// </summary>
    internal async Task<FilterDefinition<RtEntity>> BuildFilterAsync(Source source, AutoIncrementNumberReader reader,
        BsonArray? numbers)
    {
        var builder = Builders<RtEntity>.Filter;
        var filters = new List<FilterDefinition<RtEntity>>
        {
            builder.Eq(x => x.CkTypeId, source.TypeGraph.CkTypeId.ToRtCkId())
        };

        if (numbers != null)
        {
            filters.Add(new BsonDocument(source.NumberFieldPath, new BsonDocument("$in", numbers)));
        }
        else if (IsStringNumber(source))
        {
            filters.Add(new BsonDocument(source.NumberFieldPath,
                new BsonRegularExpression("^" + QuotePrefix(reader.Prefix))));
        }
        else
        {
            filters.Add(new BsonDocument(source.NumberFieldPath,
                new BsonDocument("$gte", BsonDouble.Create(double.NegativeInfinity))));
        }

        if (source.StateFieldPath != null)
        {
            filters.Add(new BsonDocument(source.StateFieldPath, new BsonDocument("$in", source.ReleasedStateValues)));
        }

        var security = DataSecurityFilterRenderer.Build<RtEntity>(await readFilter(source.TypeGraph)
            .ConfigureAwait(false));
        if (security != null)
        {
            filters.Add(security);
        }

        return builder.And(filters);
    }

    /// <summary>The aggregation of one source: the match, then only the id and the number.</summary>
    internal static PipelineDefinition<RtEntity, BsonDocument> BuildPipeline(Source source,
        FilterDefinition<RtEntity> filter) =>
        new EmptyPipelineDefinition<RtEntity>()
            .Match(filter)
            .Project<RtEntity, RtEntity, BsonDocument>(new BsonDocument
            {
                { "_id", 1 }, { NumberField, "$" + source.NumberFieldPath }
            });

    private async Task<List<NumberRangeDuplicate>> CollectDuplicatesAsync(IClientSessionHandle handle,
        IReadOnlyList<Source> sources, AutoIncrementNumberReader reader, AutoIncrementRangeSettings settings,
        int? fiscalYear, IReadOnlyCollection<long> duplicateValues, CancellationToken cancellationToken)
    {
        if (duplicateValues.Count == 0)
        {
            return [];
        }

        var documents = duplicateValues.ToDictionary(v => v, _ => new List<RtEntityId>());
        foreach (var source in sources)
        {
            // A String number by its text; a numeric one by its value ($in compares Int32 and Int64 by value).
            var numbers = new BsonArray(duplicateValues.Select(v => IsStringNumber(source)
                ? (BsonValue)Formatted(settings, v, fiscalYear)
                : new BsonInt64(v)));

            var filter = await BuildFilterAsync(source, reader, numbers).ConfigureAwait(false);
            using var cursor = await source.Collection.AggregateAsync(handle, BuildPipeline(source, filter),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            while (await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
            {
                foreach (var row in cursor.Current)
                {
                    if (TryReadValue(source, reader, row, out var value) && documents.TryGetValue(value, out var list))
                    {
                        list.Add(DocumentId(source, row));
                    }
                }
            }
        }

        return documents
            .Select(d => new NumberRangeDuplicate(d.Key, Formatted(settings, d.Key, fiscalYear),
                d.Value.OrderBy(id => id.RtId.ToString(), StringComparer.Ordinal).ToList()))
            .ToList();
    }

    private static List<NumberRangeGap> MissingIntervals(BitArray seen, long startValue,
        AutoIncrementRangeSettings settings, int? fiscalYear)
    {
        var gaps = new List<NumberRangeGap>();
        var index = 0;
        while (index < seen.Length)
        {
            if (seen[index])
            {
                index++;
                continue;
            }

            var first = index;
            while (index < seen.Length && !seen[index])
            {
                index++;
            }

            var from = startValue + first;
            var to = startValue + index - 1;
            gaps.Add(new NumberRangeGap(from, to, Formatted(settings, from, fiscalYear),
                Formatted(settings, to, fiscalYear)));
        }

        return gaps;
    }

    private async Task<RtAutoIncrement?> FindCounterAsync(IClientSessionHandle handle, string numberRange,
        CancellationToken cancellationToken)
    {
        var counterGraph = ckCacheService.GetRtCkType(tenantId, SystemCkIds.CkAutoIncrementTypeId.ToRtCkId());
        var collection = ((IMongoDbDataSourceCollection<OctoObjectId, RtAutoIncrement>)dataSource
            .GetRtCollection<RtAutoIncrement>(counterGraph)).GetMongoCollection();
        var filter = Builders<RtAutoIncrement>.Filter.Eq(c => c.RtWellKnownName, numberRange);
        var security = DataSecurityFilterRenderer.Build<RtAutoIncrement>(await readFilter(counterGraph)
            .ConfigureAwait(false));
        if (security != null)
        {
            filter = Builders<RtAutoIncrement>.Filter.And(filter, security);
        }

        return await collection.Find(handle, filter).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     The stored values of the released states: for an enum attribute the keys of the CK enum values named in
    ///     <c>ReleasedValues</c> (and the names, for a value stored by name), for a string attribute the names.
    /// </summary>
    private BsonArray ReleasedStateValues(NumberRangeRelease release, CkTypeAttributeGraph stateAttribute)
    {
        var values = new BsonArray(release.ReleasedValues.OrderBy(v => v, StringComparer.Ordinal));
        if (stateAttribute.ValueType == AttributeValueTypesDto.Enum && stateAttribute.ValueCkEnumId != null &&
            ckCacheService.TryGetCkEnum(tenantId, stateAttribute.ValueCkEnumId, out CkEnumGraph? stateEnum))
        {
            foreach (var value in stateEnum.Values.Where(v => release.ReleasedValues.Contains(v.Name)))
            {
                values.Add(value.Key);
            }
        }

        return values;
    }

    private static bool IsStringNumber(Source source) =>
        source.NumberAttribute.ValueType == AttributeValueTypesDto.String;

    private static bool TryReadValue(Source source, AutoIncrementNumberReader reader, BsonDocument row,
        out long value)
    {
        value = 0;
        if (!row.TryGetValue(NumberField, out var number))
        {
            return false;
        }

        if (IsStringNumber(source))
        {
            return number.IsString && reader.TryRead(number.AsString, out value);
        }

        if (number is BsonInt32 or BsonInt64)
        {
            value = number.ToInt64();
            return true;
        }

        return false;
    }

    private static string NumberText(BsonDocument row) =>
        row.TryGetValue(NumberField, out var number)
            ? number.IsString ? number.AsString : number.ToString() ?? string.Empty
            : string.Empty;

    private static RtEntityId DocumentId(Source source, BsonDocument row) =>
        new(source.TypeGraph.CkTypeId.ToRtCkId(), new OctoObjectId(row["_id"].AsObjectId.ToString()));

    private static string Formatted(AutoIncrementRangeSettings settings, long value, int? fiscalYear) =>
        settings.Format.IsSet ? settings.Format.Format(value, fiscalYear) : value.ToString(CultureInfo.InvariantCulture);

    private static string AttributeField(string attributeName) =>
        AttributesField + "." + attributeName.ToCamelCase();

    /// <summary>
    ///     The prefix as literal regex text: <c>\Q...\E</c>, which MongoDB reads as a simple prefix and turns into tight
    ///     index bounds; a prefix that holds <c>\E</c> itself is escaped character by character instead.
    /// </summary>
    internal static string QuotePrefix(string prefix)
    {
        if (prefix.Length == 0)
        {
            return string.Empty;
        }

        return prefix.Contains(@"\E", StringComparison.Ordinal)
            ? System.Text.RegularExpressions.Regex.Escape(prefix)
            : @"\Q" + prefix + @"\E";
    }
}
