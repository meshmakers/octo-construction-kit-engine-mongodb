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
    ///     True when the snapshot line is a document owned by the System model (the <c>CkModel</c> row itself or a
    ///     row whose <c>ckModelId</c> is a System version). Those rows follow the embedded System (AB#6929), not the
    ///     model under test.
    /// </summary>
    internal static bool IsOwnedBySystem(string line)
    {
        var separator = line.IndexOf(' ');
        var document = BsonDocument.Parse(line[(separator + 1)..]);
        var owner = line[..separator] == "CkModel" ? document["_id"] : document.GetValue("ckModelId", BsonNull.Value);
        return owner.IsString && owner.AsString.StartsWith("System-", StringComparison.Ordinal);
    }

    /// <summary>
    ///     Replaces every concrete System version (<c>System-2.5.0</c>) by <c>System-*</c>: a golden file written
    ///     against one embedded System must stay valid for the next (AB#6929).
    /// </summary>
    internal static string MaskSystemVersion(string line) =>
        SystemVersionPattern.Replace(line, "System-*");

    private static readonly System.Text.RegularExpressions.Regex SystemVersionPattern =
        new(@"System-\d+\.\d+\.\d+", System.Text.RegularExpressions.RegexOptions.Compiled);

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
