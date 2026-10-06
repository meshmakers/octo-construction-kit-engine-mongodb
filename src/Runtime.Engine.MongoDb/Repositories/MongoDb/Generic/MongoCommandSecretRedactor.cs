using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Serialization;

using MongoDB.Bson;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

/// <summary>
///     Removes stored secret envelopes from MongoDB command BSON before it is rendered for
///     diagnostics - the slow-query buffer served to tenant admins, the slow-query WARN log, the
///     explain preview and the driver-level debug command log (AB#5533).
/// </summary>
/// <remarks>
///     <para>
///         Two shapes are redacted: every stored secret sub-document
///         (<c>{ _t: "OctoSecret", e: "enc:v2:..." }</c>, recognisable without CK knowledge) becomes
///         <c>{ _t: "OctoSecret", e: "***" }</c>, and every string that starts with an envelope
///         prefix (<c>enc:v1:</c> / <c>enc:v2:</c>, e.g. a dotted-path <c>$set</c> or a not yet
///         migrated v1 value) becomes <c>"***"</c>.
///     </para>
///     <para>
///         Cost: the command is serialized once anyway; the document walk only runs when that JSON
///         contains <c>OctoSecret</c> or <c>enc:v</c>. The walk is copy-on-write - the input is
///         never mutated (it may be the driver's live command).
///     </para>
///     <para>
///         Limitation: a legacy plaintext value in a Secret slot cannot be told apart from any other
///         string without CK knowledge and is therefore not redacted. The engine write path always
///         protects secrets before they reach the driver, so commands issued by current code never
///         carry plaintext; only the stored state of entities the encrypt sweep has not reached yet
///         can (and those values are read, not written, by the commands rendered here).
///     </para>
/// </remarks>
internal static class MongoCommandSecretRedactor
{
    /// <summary>
    ///     Replacement for a redacted envelope.
    /// </summary>
    public const string RedactedValue = "***";

    /// <summary>
    ///     Rendered instead of a command whose redaction failed - a preview is dropped rather than
    ///     risking an unredacted one.
    /// </summary>
    public const string DroppedPreview = "<command preview dropped: secret redaction failed>";

    /// <summary>
    ///     Serializes <paramref name="command" /> to JSON with all secret envelopes redacted.
    ///     Never returns an unredacted secret: when the redaction itself fails the result is
    ///     <see cref="DroppedPreview" />. An <see cref="ObjectDisposedException" /> of the initial
    ///     serialization (disposed driver buffer) propagates so callers can keep their own message.
    /// </summary>
    public static string ToRedactedJson(BsonDocument command)
    {
        var json = command.ToJson();
        if (!MayContainSecret(json))
        {
            return json;
        }

        try
        {
            return Redact(command).ToJson();
        }
        catch (Exception)
        {
            return DroppedPreview;
        }
    }

    /// <summary>
    ///     Like <see cref="ToRedactedJson" /> but never throws (any failure yields
    ///     <see cref="DroppedPreview" />) - for logging straight from driver event callbacks.
    /// </summary>
    public static string ToRedactedJsonOrPlaceholder(BsonDocument? command)
    {
        if (command is null)
        {
            return "<null>";
        }

        try
        {
            return ToRedactedJson(command);
        }
        catch (Exception)
        {
            return DroppedPreview;
        }
    }

    /// <summary>
    ///     Cheap pre-check on serialized command JSON: false means the command certainly carries
    ///     neither a stored secret sub-document nor an envelope string.
    /// </summary>
    public static bool MayContainSecret(string json)
    {
        return json.Contains(RtSecretValueSerializer.Discriminator, StringComparison.Ordinal)
               || json.Contains("enc:v", StringComparison.Ordinal);
    }

    /// <summary>
    ///     Returns <paramref name="command" /> with all secret envelopes redacted. Copy-on-write:
    ///     the same instance is returned when nothing had to be redacted, otherwise a new document
    ///     (the input is never mutated).
    /// </summary>
    public static BsonDocument Redact(BsonDocument command)
    {
        return (BsonDocument)RedactValue(command);
    }

    private static BsonValue RedactValue(BsonValue value)
    {
        switch (value)
        {
            case BsonDocument document:
                if (RtSecretValueSerializer.IsStoredSecret(document))
                {
                    return new BsonDocument
                    {
                        { RtSecretValueSerializer.DiscriminatorElementName, RtSecretValueSerializer.Discriminator },
                        { RtSecretValueSerializer.EnvelopeElementName, RedactedValue }
                    };
                }

                return RedactDocument(document);
            case BsonArray array:
                return RedactArray(array);
            case BsonString s when IsEnvelopeString(s.Value):
                return new BsonString(RedactedValue);
            default:
                return value;
        }
    }

    private static BsonValue RedactDocument(BsonDocument document)
    {
        BsonDocument? copy = null;
        var index = 0;
        foreach (var element in document)
        {
            var redacted = RedactValue(element.Value);
            if (copy is null && !ReferenceEquals(redacted, element.Value))
            {
                copy = new BsonDocument();
                var copied = 0;
                foreach (var previous in document)
                {
                    if (copied++ == index)
                    {
                        break;
                    }

                    copy.Add(previous.Name, previous.Value);
                }
            }

            copy?.Add(element.Name, redacted);
            index++;
        }

        return copy ?? document;
    }

    private static BsonValue RedactArray(BsonArray array)
    {
        BsonArray? copy = null;
        for (var i = 0; i < array.Count; i++)
        {
            var item = array[i];
            var redacted = RedactValue(item);
            if (copy is null && !ReferenceEquals(redacted, item))
            {
                copy = new BsonArray(array.Count);
                for (var j = 0; j < i; j++)
                {
                    copy.Add(array[j]);
                }
            }

            copy?.Add(redacted);
        }

        return copy ?? array;
    }

    private static bool IsEnvelopeString(string value)
    {
        return value.StartsWith(SecretEnvelope.PrefixV2, StringComparison.Ordinal)
               || value.StartsWith(SecretEnvelope.PrefixV1, StringComparison.Ordinal);
    }
}
