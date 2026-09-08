using System.Collections;
using System.Dynamic;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Runtime.Contracts.Geospatial.Geometry;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Options;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver.GeoJsonObjectModel;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Serialization;

// The value serializer for the attribute dictionary is pinned to an OctoObjectSerializer with our
// own discriminator convention + allowed-types predicate, rather than relying on whatever is
// globally registered for `typeof(object)`. A process-global registration race can otherwise leave
// the MongoDB driver's default ObjectSerializer (framework-types only) in place, which rejects
// RtRecord during deserialization of attribute values (CI build 36440). Pinning makes the read
// path deterministic regardless of registration order.
internal class RtAttributeDictionarySerializer()
    : DictionarySerializerBase<Dictionary<string, object?>>(
        DictionaryRepresentation.Document,
        new StringSerializer(),
        new OctoObjectSerializer(
            new RtEntityDiscriminatorConvention("_t"),
            GuidRepresentation.Unspecified,
            MongoRepositoryClient.OctoObjectAllowedTypes))
{
    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args,
        Dictionary<string, object?>? value)
    {
        if (value is { Count: > 0 })
        {
            var dic = value.ToDictionary(d => d.Key.ToCamelCase(), d => d.Value);

            var bsonWriter = context.Writer;
            bsonWriter.WriteStartDocument();

            foreach (var keyValuePair in dic)
            {
                bsonWriter.WriteName(keyValuePair.Key);
                if (keyValuePair.Value == null)
                {
                    bsonWriter.WriteNull();
                    continue;
                }

                switch (keyValuePair.Value)
                {
                    case IEnumerable<string> enumerable:
                        bsonWriter.WriteStartArray();
                        foreach (var item in enumerable)
                        {
                            bsonWriter.WriteString(item);
                        }

                        bsonWriter.WriteEndArray();
                        break;
                    case IEnumerable<RtRecord> enumerable:
                        bsonWriter.WriteStartArray();
                        var recordSerializer = BsonSerializer.LookupSerializer(typeof(RtRecord));
                        foreach (var item in enumerable)
                        {
                            recordSerializer.Serialize(context, args, item);
                        }

                        bsonWriter.WriteEndArray();
                        break;
                    case var _ when IsArrayAttributeValue(keyValuePair.Value):
                        SerializeArrayValue(context, (IEnumerable)keyValuePair.Value);
                        break;
                    default:
                        if (keyValuePair.Value is Point p)
                        {
                            var jsonPoint = p.ToGeoJsonPoint();
                            var pointSerializer = BsonSerializer.LookupSerializer(jsonPoint.GetType());
                            pointSerializer.Serialize(context, args, jsonPoint);
                        }
                        else
                        {
                            var actualType = keyValuePair.Value.GetType();
                            var serializer = BsonSerializer.LookupSerializer(actualType);
                            serializer.Serialize(context, args, keyValuePair.Value);
                        }

                        break;
                }
            }

            bsonWriter.WriteEndDocument();
        }
        else
        {
            // AB#5148: an attribute-less entity must persist `attributes: {}`, never an explicit
            // null. MongoDB cannot create a field beneath a null value (write error code 28), so
            // a null here poisoned the entity for every later partial update that $sets an
            // `attributes.<name>` subpath. Writing an empty document keeps those updates working.
            var bsonWriter = context.Writer;
            bsonWriter.WriteStartDocument();
            bsonWriter.WriteEndDocument();
        }
    }

    /// <summary>
    ///     An array attribute whose value is neither <c>IEnumerable&lt;string&gt;</c> nor
    ///     <c>IEnumerable&lt;RtRecord&gt;</c> — every array attribute that has been read before being
    ///     written looks like this, because a read materializes a BSON array as
    ///     <c>List&lt;object&gt;</c>, and an <c>IntArray</c> is a <c>List&lt;long&gt;</c> in every
    ///     configuration.
    ///     <para>
    ///         Deliberately excluded: <see cref="string" /> and <c>byte[]</c> (both
    ///         <c>IEnumerable</c>, both must keep their scalar BSON representation) and dictionaries.
    ///     </para>
    /// </summary>
    private static bool IsArrayAttributeValue(object value)
    {
        return value is IEnumerable
               && value is not string
               && value is not byte[]
               && value is not IDictionary;
    }

    /// <summary>
    ///     Writes an array attribute as a BSON array, element by element through this serializer's own
    ///     pinned value serializer.
    ///     <para>
    ///         AB#5160: the previous code fell through to <c>default:</c>, looked the value's
    ///         collection serializer up in the global registry and handed it the <b>caller's</b>
    ///         <see cref="BsonSerializationArgs" /> — whose <c>NominalType</c> is the attribute
    ///         dictionary, not the value. Every driver collection serializer reads "actual type ≠
    ///         nominal type" as a polymorphic write and emits <c>{ "_t": …, "_v": [ … ] }</c> instead
    ///         of the bare array; for a generic list <see cref="RtEntityDiscriminatorConvention" />
    ///         supplies no discriminator, so not even the <c>_t</c> survived. The resulting
    ///         <c>{ "_v": [ … ] }</c> reads back as a plain <see cref="ExpandoObject" /> (no
    ///         <c>_t</c>, no <c>ckRecordId</c>) and poisons the attribute for the rest of the
    ///         entity's life: <c>GetAttributeStringValues</c>, <c>GetRtRecordAttributeValues</c> and
    ///         <c>EntityRuleEngine.SetDefaultValuesOnInsert</c> all throw on it. Whether a
    ///         <c>List&lt;object&gt;</c> escaped that fate came down to whether
    ///         <see cref="OctoObjectListSerializer" /> had won the process-global registration race
    ///         for <c>List&lt;object&gt;</c> — which is why it read as an ordering flake.
    ///     </para>
    ///     <para>
    ///         Writing the array here, with the pinned value serializer, removes both the registry
    ///         and the nominal-type dependency: the element shapes are exactly those the read path
    ///         produces.
    ///     </para>
    /// </summary>
    private void SerializeArrayValue(BsonSerializationContext context, IEnumerable value)
    {
        var bsonWriter = context.Writer;
        var itemArgs = new BsonSerializationArgs { NominalType = typeof(object) };

        bsonWriter.WriteStartArray();
        foreach (var item in value)
        {
            ValueSerializer.Serialize(context, itemArgs, item);
        }

        bsonWriter.WriteEndArray();
    }

    public override Dictionary<string, object?> Deserialize(BsonDeserializationContext context,
        BsonDeserializationArgs args)
    {
        var dic = base.Deserialize(context, args);
        if (dic == null)
        {
            return new Dictionary<string, object?>();
        }

        var ret = new Dictionary<string, object?>();
        foreach (var pair in dic)
        {
            if (pair.Value is ObjectId oid)
            {
                ret[pair.Key.ToPascalCase()] = oid.ToOctoObjectId();
                continue;
            }

            if (pair.Value is GeoJsonPoint<GeoJson2DCoordinates> p)
            {
                ret[pair.Key.ToPascalCase()] = new Point(new Position(p.Coordinates.X, p.Coordinates.Y));
                continue;
            }
            if (pair.Value is ExpandoObject expando)
            {
                var expandoDic = expando.ToDictionary();

                // AB#5160 defense in depth: documents written before the serialization fix carry the
                // driver's polymorphic wrapper (`{ "_v": [ … ] }`, discriminator-less for a generic
                // list) where an array attribute belongs. Unwrap it on read so the typed accessors
                // see a list again instead of this ExpandoObject; the next write persists it as a
                // proper BSON array and the document is healed. `_v` is never a CK attribute name —
                // an attribute id cannot start with an underscore — so this cannot shadow real data.
                if (expandoDic.Count == 1 && expandoDic.TryGetValue("_v", out var wrapped))
                {
                    ret[pair.Key.ToPascalCase()] = wrapped;
                    continue;
                }

                if (expandoDic.TryGetValue("type", out var v))
                {
                    if (v is "Point")
                    {
                        if (expandoDic["coordinates"] is List<object> coordinates)
                        {
                            var longitude = Convert.ToDouble(coordinates[0]);
                            var latitude = Convert.ToDouble(coordinates[1]);
                            double? altitude = coordinates.Count > 2 ? Convert.ToDouble(coordinates[2]) : null;
                            ret[pair.Key.ToPascalCase()] = new Point(new Position(latitude, longitude, altitude));
                            continue;
                        }
                    }
                    
                    throw new NotSupportedException($"Unsupported GeoJson type: {v}");
                }
                
            }

            // See https://stackoverflow.com/questions/66802866/why-doesnt-mongodb-c-sharp-driver-use-bsontype-decimal128-representation-for-de
            // For backward compatibility -> the driver has been serializing System.Decimal as string since before the BSON Decimal128 type was introduced
            // -> We ignore that because we introduced OctoMesh after this change.
            if (pair.Value is Decimal128 decimal128)
            {
                ret[pair.Key.ToPascalCase()] = Convert.ToDecimal(decimal128);
                continue;
            }
            
            ret[pair.Key.ToPascalCase()] = pair.Value;
        }

        return ret;
    }

    protected override Dictionary<string, object?> CreateInstance()
    {
        return new Dictionary<string, object?>();
    }
}
