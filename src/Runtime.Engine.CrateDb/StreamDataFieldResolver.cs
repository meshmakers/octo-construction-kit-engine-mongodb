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
    DataStream,

    /// <summary>Field not recognized as default or data stream</summary>
    Unknown
}

/// <summary>
/// Result of resolving a stream data field name.
/// </summary>
/// <param name="Category">Whether the field is a default, data stream, or unknown field.</param>
/// <param name="CrateDbName">
/// The CrateDB column name (concept §9, T17). For data-stream attributes this is the lower-case
/// concatenated form produced by <see cref="ColumnNameMapper.PathToColumnName"/>
/// (e.g. <c>obiscode</c>, <c>sensorreadingvalue</c>) — see that type for why the physical column
/// is lower-cased. For default fields (rtId, timestamp, ...) this is the field name as registered
/// by <c>Constants.GetDefaultStreamDataFields</c> (camelCase). Both default and data-stream
/// fields are first-class typed columns on the per-archive table — there is no longer a generic
/// <c>data</c> blob, so this is always a direct column reference.
/// </param>
/// <param name="GraphQlAlias">
/// The alias the resolver suggests for the GraphQL wire. Engine-side callers that need a wire
/// form different from the storage key (e.g. echoing the caller's requested column string) build
/// the <c>ColumnNameMapping</c> themselves rather than relying on this default — see
/// <c>StreamDataFieldResolverExtensions.ResolveToMappings</c>.
/// </param>
public record ResolvedField(
    StreamDataFieldCategory Category,
    string CrateDbName,
    string GraphQlAlias);

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
    /// <paramref name="physicalName"/> (<see cref="ComputedColumnNaming.Active"/>). Unlike the
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

        _fields[logicalName] = new ResolvedField(
            StreamDataFieldCategory.DataStream, physicalName, physicalName);
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
    /// Resolves a field name, falling back to the camelCased path for unknown fields. Use this
    /// for filter and sort paths that may reference attributes the caller didn't pre-declare —
    /// the underlying SQL column either exists on the archive table or the query fails at
    /// execution time, which is the right place to surface "unknown attribute" errors.
    /// </summary>
    public ResolvedField ResolveOrFallback(string input)
    {
        return Resolve(input) ?? new ResolvedField(
            StreamDataFieldCategory.Unknown,
            ColumnNameMapper.PathToColumnName(input),
            input.ToCamelCase());
    }

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
