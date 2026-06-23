using MongoDB.Bson;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

/// <summary>
/// Runs MongoDB's <c>$indexStats</c> aggregation across every non-system collection in a
/// tenant database and projects the results into <see cref="IndexUsageEntry"/> rows
/// (Stage 3 / AB#4224). Live-query design — the asset-repo REST endpoint calls this on
/// demand; no background polling or historical aggregation in this iteration.
/// </summary>
/// <remarks>
/// <para>
/// <c>$indexStats</c> is metadata-only — it does not touch documents, returns near-instant.
/// The collector lists collections via <c>ListCollectionNames</c>, then issues one
/// aggregation per collection. On a typical tenant DB (~10-30 collections) the whole sweep
/// completes in tens of milliseconds.
/// </para>
/// <para>
/// Replica-set aggregation: <c>$indexStats</c> returns one document per host. We group by
/// index name, sum <c>accesses.ops</c> across hosts (any host's hit counts as a hit), and
/// take the earliest <c>accesses.since</c> (the longest observation window — if an index was
/// added recently on a secondary, the primary's older "since" is the operator-actionable
/// figure).
/// </para>
/// </remarks>
public static class IndexUsageCollector
{
    /// <summary>
    /// Collections we skip — Mongo-managed system metadata where index analysis makes no
    /// sense or would produce noise. <c>__schema</c> is internal to some driver
    /// installations.
    /// </summary>
    private static readonly string[] SystemCollectionPrefixes = ["system.", "__"];

    /// <summary>
    /// The built-in <c>_id_</c> index every collection gets automatically. Mongo refuses
    /// <c>dropIndex("_id_")</c>; the classifier flags this as <see cref="IndexUsageStatus.Builtin"/>.
    /// </summary>
    private const string BuiltinIdIndexName = "_id_";

    /// <summary>
    /// Collects index-usage entries for every non-system collection in
    /// <paramref name="database"/>. Each entry is pre-classified using the supplied
    /// thresholds so the caller can render directly. <paramref name="now"/> is injected for
    /// deterministic age computation in tests.
    /// </summary>
    public static async Task<IReadOnlyList<IndexUsageEntry>> CollectAsync(
        IMongoDatabase database,
        int minAgeDays,
        long lowUsageOpsThreshold,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);

        var entries = new List<IndexUsageEntry>();

        var collectionNames = await ListNonSystemCollectionsAsync(database, cancellationToken).ConfigureAwait(false);
        foreach (var collectionName in collectionNames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var collection = database.GetCollection<BsonDocument>(collectionName);
            var rawStats = await RunIndexStatsAsync(collection, cancellationToken).ConfigureAwait(false);
            entries.AddRange(BuildEntries(collectionName, rawStats, minAgeDays, lowUsageOpsThreshold, now));
        }

        return entries;
    }

    /// <summary>
    /// Pure (testable) projection step: takes raw <c>$indexStats</c> documents for one
    /// collection (potentially multiple hosts × multiple indexes), aggregates per index name,
    /// builds drop-command + classification, returns the entries. Exposed internally for the
    /// replica-set-aggregation unit test.
    /// </summary>
    internal static IEnumerable<IndexUsageEntry> BuildEntries(
        string collectionName,
        IReadOnlyList<BsonDocument> rawStats,
        int minAgeDays,
        long lowUsageOpsThreshold,
        DateTimeOffset now)
    {
        // Group by index name to fold per-host rows together.
        var grouped = rawStats
            .Where(doc => doc.TryGetValue("name", out var nameVal) && nameVal.IsString)
            .GroupBy(doc => doc["name"].AsString, StringComparer.Ordinal);

        foreach (var group in grouped)
        {
            var indexName = group.Key;
            var hostDocs = group.ToList();

            // Sum ops across hosts. Use long arithmetic — a long-lived hot index can exceed
            // int range.
            long ops = 0;
            DateTimeOffset earliestSince = DateTimeOffset.MaxValue;
            string keySpec = string.Empty;

            foreach (var doc in hostDocs)
            {
                if (doc.TryGetValue("accesses", out var accessesVal) && accessesVal is BsonDocument accesses)
                {
                    if (accesses.TryGetValue("ops", out var opsVal) && opsVal.IsNumeric)
                    {
                        ops += opsVal.ToInt64();
                    }
                    if (accesses.TryGetValue("since", out var sinceVal) && sinceVal.IsValidDateTime)
                    {
                        var sinceUtc = new DateTimeOffset(sinceVal.ToUniversalTime(), TimeSpan.Zero);
                        if (sinceUtc < earliestSince)
                        {
                            earliestSince = sinceUtc;
                        }
                    }
                }

                if (keySpec.Length == 0 && doc.TryGetValue("key", out var keyVal) && keyVal is BsonDocument keyDoc)
                {
                    keySpec = keyDoc.ToJson();
                }
            }

            // If no host reported a `since` (degenerate input), fall back to `now` so age = 0
            // and the entry classifies as Used (too young to judge).
            if (earliestSince == DateTimeOffset.MaxValue)
            {
                earliestSince = now;
            }

            var ageDays = (int)Math.Max(0, (now - earliestSince).TotalDays);
            var isBuiltin = string.Equals(indexName, BuiltinIdIndexName, StringComparison.Ordinal);
            var dropCommand = isBuiltin ? null : BuildDropShellCommand(collectionName, indexName);

            var entry = new IndexUsageEntry(
                CollectionName: collectionName,
                IndexName: indexName,
                KeySpec: keySpec,
                OpsCount: ops,
                SinceUtc: earliestSince,
                AgeDays: ageDays,
                IsBuiltin: isBuiltin,
                DropShellCommand: dropCommand,
                Status: IndexUsageStatus.Used); // placeholder — overwritten below

            yield return entry with { Status = IndexUsageClassifier.Classify(entry, minAgeDays, lowUsageOpsThreshold) };
        }
    }

    private static async Task<IReadOnlyList<string>> ListNonSystemCollectionsAsync(
        IMongoDatabase database, CancellationToken ct)
    {
        var cursor = await database.ListCollectionNamesAsync(cancellationToken: ct).ConfigureAwait(false);
        var names = await cursor.ToListAsync(ct).ConfigureAwait(false);
        return names.Where(IsTenantCollection).ToList();
    }

    private static bool IsTenantCollection(string name)
    {
        foreach (var prefix in SystemCollectionPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }
        }
        return true;
    }

    private static async Task<IReadOnlyList<BsonDocument>> RunIndexStatsAsync(
        IMongoCollection<BsonDocument> collection, CancellationToken ct)
    {
        var pipeline = new BsonDocument[] { new BsonDocument("$indexStats", new BsonDocument()) };
        var cursor = await collection.AggregateAsync<BsonDocument>(pipeline, cancellationToken: ct).ConfigureAwait(false);
        return await cursor.ToListAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the paste-ready mongosh drop command. JS-string-escapes backslashes and double
    /// quotes in the collection and index names — same defensive pattern Stage 2C uses for
    /// createIndex commands. Field paths in OctoMesh are alphanumeric+dot+underscore today,
    /// but the suggester is generic over the BSON shape.
    /// </summary>
    private static string BuildDropShellCommand(string collectionName, string indexName)
        => $"db.{collectionName}.dropIndex(\"{EscapeForJsString(indexName)}\")";

    private static string EscapeForJsString(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
