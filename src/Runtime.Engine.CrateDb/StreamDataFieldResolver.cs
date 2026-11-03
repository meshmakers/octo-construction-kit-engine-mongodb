using Meshmakers.Common.Shared;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Runtime.Engine.CrateDb;

/// <summary>
/// Classification of a stream data field.
/// </summary>
public enum StreamDataFieldCategory
{
    /// <summary>A built-in default field (rtId, timestamp, etc.)</summary>
    Default,

    /// <summary>A CK model data stream attribute backed by a typed archive column</summary>
    DataStream
}

/// <summary>
/// Result of resolving a stream data field name.
/// </summary>
/// <param name="Category">Whether the field is a default, data stream, or unknown field.</param>
/// <param name="CrateDbName">
/// The <em>logical</em> column key (concept §9, T17): the name every result alias and
/// <c>StreamDataRow.Values</c> key is built from, and the key the GraphQL wire mapping looks cells up
/// by. For data-stream attributes this is the lower-case concatenated form produced by
/// <see cref="ColumnNameMapper.PathToColumnName"/> (e.g. <c>obiscode</c>, <c>sensorreadingvalue</c>) — see
/// that type for why the physical column is lower-cased. For default fields (rtId, timestamp, ...) this
/// is the field name as registered by <c>Constants.GetDefaultStreamDataFields</c> (camelCase). For every
/// column except a computed one that has been through a formula change this is also the physical column
/// name; a versioned computed column (<c>{base}__v{N}</c>, AB#8236) keeps the unversioned base here and
/// exposes the real column as <see cref="PhysicalName"/>. SQL must reference <see cref="PhysicalName"/>.
/// </param>
/// <param name="GraphQlAlias">
/// The alias the resolver suggests for the GraphQL wire. Engine-side callers that need a wire
/// form different from the storage key (e.g. echoing the caller's requested column string) build
/// the <c>ColumnNameMapping</c> themselves rather than relying on this default — see
/// <c>StreamDataFieldResolverExtensions.ResolveToMappings</c>.
/// </param>
/// <param name="PhysicalColumnName">
/// The column as it exists in the CrateDB table when it differs from <paramref name="CrateDbName"/>
/// (a versioned computed column); <c>null</c> otherwise. Read it through <see cref="PhysicalName"/>.
/// </param>
public record ResolvedField(
    StreamDataFieldCategory Category,
    string CrateDbName,
    string GraphQlAlias,
    string? PhysicalColumnName = null)
{
    /// <summary>
    /// The column name to put into SQL (SELECT / WHERE / ORDER BY / aggregate arguments). Equals
    /// <see cref="CrateDbName"/> except for a computed column with a formula history, where it is the
    /// versioned <c>{base}__v{N}</c> column. The physical name must never reach a result alias
    /// (AB#8236) — a second formula change would rename the alias again and break saved queries.
    /// </summary>
    public string PhysicalName => PhysicalColumnName ?? CrateDbName;
}

/// <summary>
/// Central resolver for stream data field names. Maps input attribute paths (in any casing) to
/// their canonical CrateDB column name and GraphQL alias. Per the T17 hard cut every attribute
/// has its own typed column, so the dotted path <c>sensor.reading.value</c> resolves to column
/// <c>sensorReadingValue</c> via <see cref="ColumnNameMapper"/>; the legacy
/// <c>data['attribute']</c> indirection is gone.
/// </summary>
public class StreamDataFieldResolver
{
    private readonly Dictionary<string, ResolvedField> _fields = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Creates a new resolver with the given data stream attribute paths. Default fields are
    /// registered from <see cref="Constants.GetDefaultStreamDataFields"/> based on the storage
    /// shape — raw archives get the <c>timestamp</c>-based defaults, windowed (rollup +
    /// time-range) archives get <c>window_start</c> / <c>window_end</c> / <c>was_updated</c>.
    /// </summary>
    /// <param name="dataStreamAttributePaths">
    /// Attribute paths from the CK model. May be dotted (e.g. <c>sensor.reading.value</c>); each
    /// path is mapped to its column name via <see cref="ColumnNameMapper.PathToColumnName"/>.
    /// </param>
    /// <param name="usesWindowedStorage">
    /// True for rollup and time-range archives (windowed storage shape), false for raw archives.
    /// Mirrors <see cref="ArchiveSnapshot.UsesWindowedStorage"/>.
    /// </param>
    public StreamDataFieldResolver(IEnumerable<string> dataStreamAttributePaths, bool usesWindowedStorage = false)
    {
        // Register default fields from Constants (single source of truth, already camelCase).
        foreach (var defaultField in Constants.GetDefaultStreamDataFields(usesWindowedStorage))
        {
            _fields[defaultField] = new ResolvedField(
                StreamDataFieldCategory.Default,
                defaultField,
                defaultField);
        }

        // Register data stream attributes
        foreach (var path in dataStreamAttributePaths)
        {
            // Don't overwrite defaults if a data attribute happens to share the name
            if (Constants.IsDefaultField(path))
            {
                continue;
            }

            var columnName = ColumnNameMapper.PathToColumnName(path);
            _fields[path] = new ResolvedField(
                StreamDataFieldCategory.DataStream,
                columnName,
                columnName);
        }
    }

    /// <summary>
    /// Creates a resolver with no data stream attributes (only the raw-archive default fields).
    /// </summary>
    public StreamDataFieldResolver() : this([], usesWindowedStorage: false)
    {
    }

    /// <summary>
    /// Registers a computed column (AB#4189 Phase 7) under its logical <paramref name="logicalName"/>
    /// (the <c>Name</c> a query uses) mapped to its <em>versioned</em> physical column
    /// <paramref name="physicalName"/> (<see cref="ComputedColumnNaming.Active"/>), kept apart from the output key
    /// (<see cref="ResolvedField.PhysicalName"/>, AB#8236). Unlike the
    /// ctor-registered ingested columns, a computed column's physical name is not a pure function of
    /// its logical name — a formula change moves it to <c>{base}__v{N}</c> — so it must be registered
    /// explicitly rather than derived via <see cref="ColumnNameMapper.PathToColumnName"/>. Never
    /// shadows a default field.
    /// </summary>
    public void RegisterComputedColumn(string logicalName, string physicalName)
    {
        if (Constants.IsDefaultField(logicalName))
        {
            return;
        }

        // Output key = the unversioned base (what the column was called before any formula change and
        // what saved queries / widgets read); SQL goes to the active versioned physical column (AB#8236).
        var outputName = ColumnNameMapper.PathToColumnName(logicalName);
        _fields[logicalName] = new ResolvedField(
            StreamDataFieldCategory.DataStream, outputName, outputName, physicalName);
    }

    /// <summary>
    /// Resolves a field name to its canonical representation.
    /// Returns null if the field is not recognized as either a default or data stream field.
    /// </summary>
    public ResolvedField? Resolve(string input)
    {
        return _fields.GetValueOrDefault(input);
    }

    /// <summary>
    /// Every name this resolver accepts: the storage shape's default fields plus the archive's
    /// ingested and readable computed columns. Exposed so a caller that rejects an unresolvable
    /// name can say what the valid ones are — a bare "unknown column" leaves the author guessing
    /// at exactly the moment they already guessed wrong. Lookup is case-insensitive, so these are
    /// the canonical spellings rather than the only accepted ones.
    /// </summary>
    public IReadOnlyCollection<string> KnownFieldNames => _fields.Keys;

    /// <summary>
    /// The canonical way to build a resolver from an <see cref="ArchiveSnapshot"/>. Ingested
    /// columns feed the ctor's path mapping; computed columns (AB#4189) have an empty
    /// <see cref="CkArchiveColumnSpec.Path"/> — passing them through the ctor throws — and are
    /// registered explicitly under their logical <c>Name</c> mapped to the active versioned
    /// physical column (see <see cref="ReadableComputedColumns"/>). Every consumer that starts
    /// from a snapshot must go through here rather than <c>snapshot.Columns.Select(c => c.Path)</c>.
    /// </summary>
    /// <param name="archiveSnapshot">The archive whose column spec defines the queryable fields.</param>
    /// <param name="additionalPaths">
    /// Extra logical attribute paths to register alongside the snapshot's ingested columns —
    /// e.g. the rollup source-chain's logical CK paths the chain-aware aggregation resolver
    /// translates on the engine side.
    /// </param>
    public static StreamDataFieldResolver CreateForArchive(
        ArchiveSnapshot archiveSnapshot,
        IEnumerable<string>? additionalPaths = null)
    {
        var paths = archiveSnapshot.Columns.Where(c => !c.IsComputed).Select(c => c.Path);
        if (additionalPaths is not null)
        {
            paths = paths.Concat(additionalPaths);
        }

        var resolver = new StreamDataFieldResolver(
            paths,
            usesWindowedStorage: archiveSnapshot.UsesWindowedStorage);

        foreach (var (name, physical) in ReadableComputedColumns(archiveSnapshot))
        {
            resolver.RegisterComputedColumn(name, physical);
        }

        // AB#5356 / AB#7593: raw and time-range rows record their origin and, for a manual row, who wrote
        // it, when and why. Selectable by their physical names like was_updated, but not part of the
        // default projection, so existing queries return exactly the columns they did before. A rollup row has
        // no origin of its own; it reports how many of its values were entered manually (AB#7727), selectable
        // the same way.
        var platformFields = archiveSnapshot.RollupAggregations is null
            ? ArchiveRowOriginColumns.Names
            : ArchiveRowOriginFields.RollupFields;
        foreach (var name in platformFields)
        {
            resolver._fields[name] = new ResolvedField(StreamDataFieldCategory.DataStream, name, name);
        }

        return resolver;
    }

    /// <summary>
    /// The computed columns the read path may project, as (logical <c>Name</c>, active versioned
    /// physical name) pairs. A computed column mid-backfill (<see cref="ComputedColumnState.Pending"/> /
    /// <see cref="ComputedColumnState.Backfilling"/>) or whose backfill failed
    /// (<see cref="ComputedColumnState.Failed"/>) is excluded, so consumers keep seeing the previous
    /// archive state until the backfill commits (AB#4189 §8.3). A computed column created together with
    /// its archive carries no lifecycle state (<c>null</c>) and is live from creation. The ingest / DDL
    /// path (<c>CrateDbStreamDataRepository.ResolveTableAndColumns</c>) deliberately does <em>not</em>
    /// gate on state — even a Pending column's physical column exists and must be written on ingest;
    /// gating is a read concern.
    /// </summary>
    public static IEnumerable<(string Name, string Physical)> ReadableComputedColumns(ArchiveSnapshot snapshot)
    {
        foreach (var c in snapshot.Columns)
        {
            // Defensively skip a computed column missing its Name — activation DDL would have rejected
            // it, but the read path must never throw on a malformed snapshot.
            if (!c.IsComputed || string.IsNullOrWhiteSpace(c.Name))
            {
                continue;
            }

            if (c.ComputedState is null or ComputedColumnState.Active)
            {
                yield return (c.Name!, ComputedColumnNaming.Active(c));
            }
        }
    }
}
