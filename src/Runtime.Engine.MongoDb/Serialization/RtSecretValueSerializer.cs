using Meshmakers.Octo.Runtime.Contracts.MongoDb.Secrets;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Secrets;

using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Serialization;

/// <summary>
///     BSON serializer for <see cref="RtSecretValue" /> (AB#5533, concept §3.3 / §3.4).
/// </summary>
/// <remarks>
///     <para>
///         <b>Write:</b> only <see cref="RtSecretValueState.Protected" /> values are stored, as the
///         self-describing sub-document <c>{ _t: "OctoSecret", e: "enc:v2:&lt;kid&gt;:..." }</c>.
///         <see cref="RtSecretValueState.Pending" /> (a plaintext the engine write step has not
///         encrypted) and <see cref="RtSecretValueState.LegacyPlaintext" /> are refused with
///         <see cref="SecretValueNotStorableException" />, so a write path that forgets the protector
///         fails loudly instead of persisting plaintext. The emergency <c>Decrypt</c> sweep writes a
///         plain string, not an <see cref="RtSecretValue" />.
///     </para>
///     <para>
///         <b>Read:</b> the sub-document becomes <see cref="RtSecretValue.Protected" /> in every slot -
///         the <c>_t</c> discriminator makes it recognisable without CK knowledge, so a non-Secret
///         slot, a change-stream document or a dynamic read never trips over it. A BSON string read
///         through this serializer (nominal type <see cref="RtSecretValue" />) is
///         <see cref="RtSecretValue.LegacyPlaintext" /> (plaintext or <c>enc:v1</c> alike); null stays
///         null. A sub-document whose envelope is missing or not a structurally valid <c>enc:v2</c>
///         envelope reads as null ("not set"): it can never be decrypted, and failing the whole entity
///         read over it would lock the entity (same outcome as an unknown key id, decision 5).
///     </para>
///     <para>
///         The serializer is <see cref="IBsonPolymorphicSerializer" /> with
///         <see cref="IsDiscriminatorCompatibleWithObjectSerializer" /> so the object serializer used
///         for attribute values hands it the whole document (it writes and reads <c>_t</c> itself).
///     </para>
/// </remarks>
internal sealed class RtSecretValueSerializer : SerializerBase<RtSecretValue>, IBsonPolymorphicSerializer
{
    /// <summary>
    ///     Discriminator value of the stored sub-document.
    /// </summary>
    public const string Discriminator = "OctoSecret";

    /// <summary>
    ///     Element name of the discriminator.
    /// </summary>
    public const string DiscriminatorElementName = "_t";

    /// <summary>
    ///     Element name of the envelope.
    /// </summary>
    public const string EnvelopeElementName = "e";

    public bool IsDiscriminatorCompatibleWithObjectSerializer => true;

    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, RtSecretValue? value)
    {
        var writer = context.Writer;
        if (value == null)
        {
            writer.WriteNull();
            return;
        }

        if (!value.IsProtected || value.Envelope == null)
        {
            throw new SecretValueNotStorableException(value.State);
        }

        writer.WriteStartDocument();
        writer.WriteString(DiscriminatorElementName, Discriminator);
        writer.WriteString(EnvelopeElementName, value.Envelope);
        writer.WriteEndDocument();
    }

    public override RtSecretValue Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        var reader = context.Reader;
        switch (reader.GetCurrentBsonType())
        {
            case BsonType.Null:
                reader.ReadNull();
                // The base contract is non-nullable, but a null slot is a legitimate "not set".
                return null!;
            case BsonType.String:
                return RtSecretValue.LegacyPlaintext(reader.ReadString());
            case BsonType.Document:
                return ReadDocument(reader)!;
            default:
                throw new FormatException(
                    $"Cannot deserialize a secret value from BSON type '{reader.GetCurrentBsonType()}'.");
        }
    }

    private static RtSecretValue? ReadDocument(IBsonReader reader)
    {
        string? envelope = null;

        reader.ReadStartDocument();
        while (reader.ReadBsonType() != BsonType.EndOfDocument)
        {
            var name = reader.ReadName();
            if (name == EnvelopeElementName && reader.CurrentBsonType == BsonType.String)
            {
                envelope = reader.ReadString();
            }
            else
            {
                // _t (already resolved by the discriminator convention) and anything unknown.
                reader.SkipValue();
            }
        }

        reader.ReadEndDocument();

        if (envelope == null || !SecretEnvelope.TryParse(envelope, out var info) ||
            info.Version != SecretEnvelope.CurrentVersion)
        {
            return null;
        }

        return RtSecretValue.Protected(envelope);
    }

    /// <summary>
    ///     True when <paramref name="value" /> has the shape of a stored secret
    ///     (<c>_t: "OctoSecret"</c>) - for code that works on raw <see cref="BsonDocument" />s.
    /// </summary>
    public static bool IsStoredSecret(BsonValue? value)
    {
        return value is BsonDocument document
               && document.TryGetValue(DiscriminatorElementName, out var discriminator)
               && discriminator is BsonString { Value: Discriminator };
    }
}
