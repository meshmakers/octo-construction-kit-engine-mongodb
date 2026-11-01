using Meshmakers.Octo.Runtime.Contracts.Secrets;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Serialization;

using MongoDB.Bson;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

/// <summary>
///     Removes credentials from MongoDB command BSON before it is rendered for diagnostics - the
///     slow-query buffer served to tenant admins, the slow-query WARN log, the explain preview and the
///     driver-level debug command log (AB#5533, AB#8009). Every rendering of a command goes through this
///     class.
/// </summary>
/// <remarks>
///     <para>
///         Three shapes are redacted: every stored secret sub-document
///         (<c>{ _t: "OctoSecret", e: "enc:v2:..." }</c>, recognisable without CK knowledge) becomes
///         <c>{ _t: "OctoSecret", e: "***" }</c>; every string that starts with an envelope prefix
///         (<c>enc:v1:</c> / <c>enc:v2:</c>, e.g. a dotted-path <c>$set</c> or a not yet migrated v1 value)
///         becomes <c>"***"</c>; and the value of every <b>sensitive attribute</b>
///         (<see cref="MongoCommandSensitiveAttributeNames" />: Hidden, securitySensitive, Secret attributes
///         of the CK model plus the known credential fields such as <c>passwordHash</c>) is masked at every
///         level below it - field names stay, every leaf value becomes <c>"***"</c>, so the shape of the
///         command stays readable.
///     </para>
///     <para>
///         A sensitive attribute is recognised by name in the attribute context only: an element of a
///         document that is the value of an <c>attributes</c> element (an entity, a record, a <c>$set</c> of
///         the whole attribute map), or a dotted path segment that follows an <c>attributes</c> segment
///         (<c>attributes.passwordHash</c>, <c>attributes.userTokens.$[i0].attributes.value</c>,
///         <c>attributes.clientSecrets.attributes.value</c> in a filter). Inserts, updates, filters,
///         <c>findAndModify</c> and aggregation stages are all covered by that rule.
///     </para>
///     <para>
///         Cost: the command is serialized once anyway; the document walk only runs when that JSON contains
///         <c>OctoSecret</c> / <c>enc:v</c>, or contains <c>attributes</c> and one of the sensitive names. The
///         walk is copy-on-write - the input is never mutated (it may be the driver's live command).
///     </para>
///     <para>
///         Limitation: a legacy plaintext value in a Secret slot of an attribute that is neither in the CK
///         cache of this process nor a known credential name cannot be told apart from any other string and
///         is therefore not redacted.
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
    ///     Name of the document that holds the attribute values of an entity or a record.
    /// </summary>
    private const string AttributesElementName = "attributes";

    /// <summary>
    ///     Serializes <paramref name="command" /> to JSON with all secret envelopes and the values of all
    ///     sensitive attributes redacted (<paramref name="sensitiveAttributeNames" />, default
    ///     <see cref="MongoCommandSensitiveAttributeNames.KnownCredentialNames" />).
    ///     Never returns an unredacted secret: when the redaction itself fails the result is
    ///     <see cref="DroppedPreview" />. An <see cref="ObjectDisposedException" /> of the initial
    ///     serialization (disposed driver buffer) propagates so callers can keep their own message.
    /// </summary>
    public static string ToRedactedJson(BsonDocument command, IReadOnlySet<string>? sensitiveAttributeNames = null)
    {
        var names = sensitiveAttributeNames ?? MongoCommandSensitiveAttributeNames.KnownCredentialNames;
        var json = command.ToJson();
        if (!MayContainSecret(json) && !MayContainSensitiveAttribute(json, names))
        {
            return json;
        }

        try
        {
            return Redact(command, names).ToJson();
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
    public static string ToRedactedJsonOrPlaceholder(BsonDocument? command,
        IReadOnlySet<string>? sensitiveAttributeNames = null)
    {
        if (command is null)
        {
            return "<null>";
        }

        try
        {
            return ToRedactedJson(command, sensitiveAttributeNames);
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
    ///     Cheap pre-check on serialized command JSON: false means the command certainly carries no
    ///     value of one of <paramref name="sensitiveAttributeNames" /> in an attribute context.
    /// </summary>
    public static bool MayContainSensitiveAttribute(string json, IReadOnlySet<string> sensitiveAttributeNames)
    {
        if (sensitiveAttributeNames.Count == 0 || !json.Contains(AttributesElementName, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var name in sensitiveAttributeNames)
        {
            if (json.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Returns <paramref name="command" /> with all secret envelopes and the values of all sensitive
    ///     attributes redacted (default names: <see cref="MongoCommandSensitiveAttributeNames.KnownCredentialNames" />).
    ///     Copy-on-write: the same instance is returned when nothing had to be redacted, otherwise a new
    ///     document (the input is never mutated).
    /// </summary>
    public static BsonDocument Redact(BsonDocument command, IReadOnlySet<string>? sensitiveAttributeNames = null)
    {
        var names = sensitiveAttributeNames ?? MongoCommandSensitiveAttributeNames.KnownCredentialNames;
        return (BsonDocument)RedactValue(command, inAttributes: false, names);
    }

    private static BsonValue RedactValue(BsonValue value, bool inAttributes, IReadOnlySet<string> names)
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

                return RedactDocument(document, inAttributes, names);
            case BsonArray array:
                return RedactArray(array, names);
            case BsonString s when IsEnvelopeString(s.Value):
                return new BsonString(RedactedValue);
            default:
                return value;
        }
    }

    private static BsonValue RedactDocument(BsonDocument document, bool inAttributes, IReadOnlySet<string> names)
    {
        BsonDocument? copy = null;
        var index = 0;
        foreach (var element in document)
        {
            var redacted = IsSensitiveElementName(element.Name, inAttributes, names)
                ? MaskAll(element.Value)
                : RedactValue(element.Value, EndsWithAttributesSegment(element.Name), names);
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

    private static BsonValue RedactArray(BsonArray array, IReadOnlySet<string> names)
    {
        BsonArray? copy = null;
        for (var i = 0; i < array.Count; i++)
        {
            var item = array[i];
            // Array items are never in an attribute context themselves: an entity, a record or a filter
            // in an array opens its own context through its "attributes" element or a dotted path.
            var redacted = RedactValue(item, inAttributes: false, names);
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

    /// <summary>
    ///     True when <paramref name="elementName" /> (possibly a dotted path) names a sensitive attribute:
    ///     its first segment while the element sits in an attribute context, or any segment that follows an
    ///     <c>attributes</c> segment.
    /// </summary>
    internal static bool IsSensitiveElementName(string elementName, bool inAttributes, IReadOnlySet<string> names)
    {
        if (names.Count == 0)
        {
            return false;
        }

        if (!elementName.Contains('.', StringComparison.Ordinal))
        {
            return inAttributes && names.Contains(elementName);
        }

        var segments = elementName.Split('.');
        for (var i = 0; i < segments.Length; i++)
        {
            var inContext = i == 0
                ? inAttributes
                : string.Equals(segments[i - 1], AttributesElementName, StringComparison.Ordinal);
            if (inContext && names.Contains(segments[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool EndsWithAttributesSegment(string elementName)
    {
        return string.Equals(elementName, AttributesElementName, StringComparison.Ordinal)
               || elementName.EndsWith("." + AttributesElementName, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Replaces every leaf below <paramref name="value" /> by <see cref="RedactedValue" />; document field
    ///     names and array lengths stay. Always returns a new value.
    /// </summary>
    private static BsonValue MaskAll(BsonValue value)
    {
        switch (value)
        {
            case BsonDocument document:
                var masked = new BsonDocument();
                foreach (var element in document)
                {
                    masked.Add(element.Name, MaskAll(element.Value));
                }

                return masked;
            case BsonArray array:
                var maskedArray = new BsonArray(array.Count);
                foreach (var item in array)
                {
                    maskedArray.Add(MaskAll(item));
                }

                return maskedArray;
            default:
                return new BsonString(RedactedValue);
        }
    }

    private static bool IsEnvelopeString(string value)
    {
        return value.StartsWith(SecretEnvelope.PrefixV2, StringComparison.Ordinal)
               || value.StartsWith(SecretEnvelope.PrefixV1, StringComparison.Ordinal);
    }
}
