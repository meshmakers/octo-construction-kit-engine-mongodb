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
            var bsonWriter = context.Writer;
            bsonWriter.WriteNull();
        }
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
