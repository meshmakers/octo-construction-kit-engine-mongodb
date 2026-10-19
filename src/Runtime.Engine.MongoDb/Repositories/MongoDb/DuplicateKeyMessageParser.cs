using System.Text.RegularExpressions;

using MongoDB.Bson;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

/// <summary>
///     Reads the index name and the duplicated key out of the text of a MongoDB duplicate key error
///     (<c>E11000 duplicate key error collection: db.coll index: name dup key: { field: "value" }</c>), AB#7018.
///     The driver exposes no structured form of it for a bulk write.
/// </summary>
internal static partial class DuplicateKeyMessageParser
{
    internal readonly record struct ParsedDuplicateKey(string? IndexName, string? DupKey);

    [GeneratedRegex(@"index:\s+(?<index>\S+)\s+dup key:\s*(?<key>\{.*\})", RegexOptions.Singleline)]
    private static partial Regex DuplicateKeyRegex();

    // The server prints unquoted field names ({ attributes.code: "x" }); JSON needs them quoted.
    [GeneratedRegex(@"(?<open>[{,]\s*)(?<name>[^\s:""{},]+)\s*:")]
    private static partial Regex UnquotedFieldRegex();

    internal static ParsedDuplicateKey Parse(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return new ParsedDuplicateKey(null, null);
        }

        var match = DuplicateKeyRegex().Match(message);
        return match.Success
            ? new ParsedDuplicateKey(match.Groups["index"].Value, match.Groups["key"].Value)
            : new ParsedDuplicateKey(null, null);
    }

    /// <summary>
    ///     An equality filter over the fields of the duplicated key, or <c>null</c> when the key text cannot be
    ///     read (the caller then reports the state as unknown).
    /// </summary>
    internal static FilterDefinition<TDocument>? BuildKeyFilter<TDocument>(string? dupKey)
    {
        if (string.IsNullOrEmpty(dupKey))
        {
            return null;
        }

        var quoted = UnquotedFieldRegex().Replace(dupKey, "${open}\"${name}\":");
        if (!BsonDocument.TryParse(quoted, out var key) || key.ElementCount == 0)
        {
            return null;
        }

        return Builders<TDocument>.Filter.And(
            key.Select(e => Builders<TDocument>.Filter.Eq<BsonValue>(e.Name, e.Value)));
    }
}
