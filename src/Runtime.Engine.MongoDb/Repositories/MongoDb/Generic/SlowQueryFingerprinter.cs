using System.Security.Cryptography;
using System.Text;

using MongoDB.Bson;
using MongoDB.Bson.IO;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

/// <summary>
/// Computes a stable structural fingerprint of a BSON command so that semantically-identical
/// queries that differ only in literal values group under the same identifier. The fingerprint
/// is the join key for the upcoming explain-capture and index-suggestion stages of the
/// Performance Advisor.
/// </summary>
/// <remarks>
/// Algorithm:
/// <list type="number">
///   <item>Walk the BSON document recursively, replacing every primitive value (string, int,
///         double, bool, ObjectId, DateTime, Binary, Null, etc.) with the literal token
///         <c>"?"</c>.</item>
///   <item>Preserve field names, field order, stage order, and nested document structure.
///         Field order matters in MongoDB (e.g. <c>{a:1, b:2}</c> can be a different query
///         from <c>{b:2, a:1}</c> for some operators), so we use <c>BsonDocument.Elements</c>'s
///         insertion order, never sort.</item>
///   <item>Collapse arrays to a single placeholder element (<c>[1,2,3]</c> →
///         <c>["?"]</c>). Arrays-of-arrays / arrays-of-documents recurse before the collapse,
///         so a one-element array is kept structurally if its single element is a document.</item>
///   <item>Serialise the normalised BSON to canonical extended JSON (deterministic
///         representation that doesn't drift across driver versions for the placeholder set).</item>
///   <item>SHA-256 over the JSON bytes; the fingerprint is the first 16 hex characters
///         (64 bits — birthday-collision-safe at the per-tenant buffer scale of 1000 entries).</item>
/// </list>
/// </remarks>
public static class SlowQueryFingerprinter
{
    private const string Placeholder = "?";

    /// <summary>Length of the returned hex fingerprint.</summary>
    public const int FingerprintLength = 16;

    /// <summary>
    /// Returns a stable 16-character hex fingerprint of <paramref name="command"/>, or
    /// <c>"00000000"</c> when the command is null or empty (the caller should treat this as
    /// "unknown shape" rather than a real grouping key).
    /// </summary>
    public static string Fingerprint(BsonDocument? command)
    {
        if (command is null || command.ElementCount == 0)
        {
            return new string('0', FingerprintLength);
        }

        var normalised = Normalise(command);
        var json = normalised.ToJson(new JsonWriterSettings { OutputMode = JsonOutputMode.CanonicalExtendedJson });
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        var hex = Convert.ToHexString(bytes, 0, FingerprintLength / 2);
        return hex.ToLowerInvariant();
    }

    private static BsonValue Normalise(BsonValue value) => value.BsonType switch
    {
        BsonType.Document => NormaliseDocument(value.AsBsonDocument),
        BsonType.Array => NormaliseArray(value.AsBsonArray),
        _ => new BsonString(Placeholder)
    };

    private static BsonDocument NormaliseDocument(BsonDocument doc)
    {
        var result = new BsonDocument();
        // Elements iterates in insertion order — preserve it so field-order-sensitive queries
        // get distinct fingerprints.
        foreach (var element in doc.Elements)
        {
            result.Add(element.Name, Normalise(element.Value));
        }

        return result;
    }

    private static BsonArray NormaliseArray(BsonArray array)
    {
        if (array.Count == 0)
        {
            // Empty array stays empty — captures the structural fact that the slot is an array.
            return new BsonArray();
        }

        // Two array shapes matter:
        //
        // 1. Arrays of documents (aggregation pipelines like
        //    [{$match:...}, {$project:...}, {$group:...}]). Stage count and stage order are
        //    structurally significant — a 3-stage pipeline must not fingerprint the same as a
        //    1-stage one. Preserve every element, recurse into each.
        //
        // 2. Arrays of primitives ($in/$nin/$all value lists). Element count is just data and
        //    should not split fingerprints — [1,2,3] vs [4,5] are the same query shape.
        //    Collapse to a single placeholder.
        //
        // Heuristic: if the FIRST element is a document, treat the whole array as a structural
        // sequence and recurse into all elements. Otherwise, collapse. Mixed-type arrays are
        // rare in real MongoDB queries; if encountered the document-first branch errs on the
        // side of preserving structure.
        if (array[0].BsonType == BsonType.Document)
        {
            var preserved = new BsonArray();
            foreach (var element in array)
            {
                preserved.Add(Normalise(element));
            }

            return preserved;
        }

        return new BsonArray { Normalise(array[0]) };
    }
}
