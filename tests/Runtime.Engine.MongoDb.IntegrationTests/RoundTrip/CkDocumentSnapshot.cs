using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.RoundTrip;

/// <summary>
///     CK v2 F1.3-S4 (AB#5917): a canonical, byte-comparable snapshot of every <c>Ck*</c> document a tenant holds for
///     the given models. Used to prove that a v1 import through the Phase 1 engine writes exactly the documents the
///     main engine writes (rollback safety). Generated ObjectId <c>_id</c>s (association, inheritance and
///     implementation rows) are dropped; documents are sorted by their canonical JSON.
/// </summary>
internal static class CkDocumentSnapshot
{
    internal static readonly string[] Collections =
    [
        "CkModel", "CkType", "CkRecord", "CkEnum", "CkAttribute", "CkAssociationRole", "CkTypeAssociation",
        "CkTypeInheritance", "CkRecordInheritance", "CkInterface", "CkTypeInterfaceImplementation"
    ];

    internal static async Task<string> TakeAsync(IMongoDatabase database, IReadOnlyCollection<string> modelIds,
        CancellationToken cancellationToken)
    {
        var lines = new List<string>();
        var existing = await (await database.ListCollectionNamesAsync(cancellationToken: cancellationToken))
            .ToListAsync(cancellationToken);
        foreach (var collection in Collections.Where(existing.Contains))
        {
            var filter = collection == "CkModel"
                ? new BsonDocument("_id", new BsonDocument("$in", new BsonArray(modelIds)))
                : new BsonDocument("ckModelId", new BsonDocument("$in", new BsonArray(modelIds)));
            var documents = await database.GetCollection<BsonDocument>(collection).Find(filter)
                .ToListAsync(cancellationToken);
            foreach (var document in documents)
            {
                Normalize(document);

                lines.Add($"{collection} {document.ToJson(new JsonWriterSettings { OutputMode = JsonOutputMode.CanonicalExtendedJson })}");
            }
        }

        lines.Sort(StringComparer.Ordinal);
        return string.Join("\n", lines) + "\n";
    }

    /// <summary>
    ///     Re-normalizes one snapshot line (e.g. of a golden file written before a normalization rule existed).
    /// </summary>
    internal static string NormalizeLine(string line)
    {
        var separator = line.IndexOf(' ');
        var document = BsonDocument.Parse(line[(separator + 1)..]);
        Normalize(document);
        return $"{line[..separator]} {document.ToJson(new JsonWriterSettings { OutputMode = JsonOutputMode.CanonicalExtendedJson })}";
    }

    /// <summary>
    ///     Removes what differs between two imports of the same model by design: generated ObjectId <c>_id</c>s and
    ///     the <c>appliedAt</c> timestamp of index states.
    /// </summary>
    private static void Normalize(BsonDocument document)
    {
        if (document.TryGetValue("_id", out var id) && id.IsObjectId)
        {
            document.Remove("_id");
        }

        if (document.TryGetValue("indexStates", out var states) && states.IsBsonArray)
        {
            foreach (var state in states.AsBsonArray.Where(s => s.IsBsonDocument))
            {
                state.AsBsonDocument.Remove("appliedAt");
            }
        }
    }
}
