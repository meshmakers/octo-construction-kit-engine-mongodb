using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Serialization;

internal class DateTimeOffsetSerializer : StructSerializerBase<DateTimeOffset>, IRepresentationConfigurable<DateTimeOffsetSerializer>
{
     // private constants
        private static class Flags
        {
            public const long DateTime = 1;
            public const long Ticks = 2;
            public const long Offset = 4;
        }

        // private fields
        private readonly SerializerHelper _helper;
        private readonly BsonType _representation;

        // constructors
        /// <summary>
        /// Initializes a new instance of the <see cref="DateTimeOffsetSerializer"/> class.
        /// </summary>
        public DateTimeOffsetSerializer()
            : this(BsonType.Document)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="DateTimeOffsetSerializer"/> class.
        /// </summary>
        /// <param name="representation">The representation.</param>
        public DateTimeOffsetSerializer(BsonType representation)
        {
            switch (representation)
            {
                case BsonType.Array:
                case BsonType.DateTime:
                case BsonType.Document:
                case BsonType.String:
                    break;

                default:
                    var message = $"{representation} is not a valid representation for a DateTimeOffsetSerializer.";
                    throw new ArgumentException(message);
            }

            _representation = representation;

            _helper = new SerializerHelper
            (
                new SerializerHelper.Member("dateTime", Flags.DateTime),
                new SerializerHelper.Member("ticks", Flags.Ticks),
                new SerializerHelper.Member("offset", Flags.Offset)
            );
        }

        // public properties
        /// <summary>
        /// Gets the representation.
        /// </summary>
        /// <value>
        /// The representation.
        /// </value>
        public BsonType Representation => _representation;

        // public methods
        /// <summary>
        /// Deserializes a value.
        /// </summary>
        /// <param name="context">The deserialization context.</param>
        /// <param name="args">The deserialization args.</param>
        /// <returns>A deserialized value.</returns>
        public override DateTimeOffset Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
        {
            var bsonReader = context.Reader;
            long ticks;
            TimeSpan offset;

            BsonType bsonType = bsonReader.GetCurrentBsonType();
            switch (bsonType)
            {
                case BsonType.Array:
                    bsonReader.ReadStartArray();
                    ticks = Int64Serializer.Instance.Deserialize(context);
                    offset = TimeSpan.FromMinutes(Int32Serializer.Instance.Deserialize(context));
                    bsonReader.ReadEndArray();
                    return new DateTimeOffset(ticks, offset);

                case BsonType.DateTime:
                    var millisecondsSinceEpoch = bsonReader.ReadDateTime();
                    return DateTimeOffset.FromUnixTimeMilliseconds(millisecondsSinceEpoch);

                case BsonType.Document:
                    ticks = 0;
                    offset = TimeSpan.Zero;
                    _helper.DeserializeMembers(context, (elementName, flag) =>
                    {
                        switch (flag)
                        {
                            case Flags.DateTime: bsonReader.SkipValue(); break; // ignore value
                            case Flags.Ticks: ticks = Int64Serializer.Instance.Deserialize(context); break;
                            case Flags.Offset: offset = TimeSpan.FromMinutes(Int32Serializer.Instance.Deserialize(context)); break;
                        }
                    });
                    return new DateTimeOffset(ticks, offset);

                case BsonType.String:
                    return JsonConvert.ToDateTimeOffset(bsonReader.ReadString());

                default:
                    throw CreateCannotDeserializeFromBsonTypeException(bsonType);
            }
        }

        /// <inheritdoc/>
        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(obj, null))
            {
                return false;
            }

            if (ReferenceEquals(this, obj))
            {
                return true;
            }
            
            return base.Equals(obj) &&
                   obj is DateTimeOffsetSerializer other &&
                   _representation.Equals(other._representation);
        }

        /// <inheritdoc/>
        public override int GetHashCode() => 0;

        /// <summary>
        /// Serializes a value.
        /// </summary>
        /// <param name="context">The serialization context.</param>
        /// <param name="args">The serialization args.</param>
        /// <param name="value">The object.</param>
        public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, DateTimeOffset value)
        {
            var bsonWriter = context.Writer;

            switch (_representation)
            {
                case BsonType.Array:
                    bsonWriter.WriteStartArray();
                    bsonWriter.WriteInt64(value.Ticks);
                    bsonWriter.WriteInt32((int)value.Offset.TotalMinutes);
                    bsonWriter.WriteEndArray();
                    break;

                case BsonType.DateTime:
                    var millisecondsSinceEpoch = value.ToUnixTimeMilliseconds();
                    bsonWriter.WriteDateTime(millisecondsSinceEpoch);
                    break;

                case BsonType.Document:
                    bsonWriter.WriteStartDocument();
                    bsonWriter.WriteString("_t", "datetimeoffset");
                    bsonWriter.WriteStartDocument("_v");
                    bsonWriter.WriteDateTime("dateTime", BsonUtils.ToMillisecondsSinceEpoch(value.UtcDateTime));
                    bsonWriter.WriteInt64("ticks", value.Ticks);
                    bsonWriter.WriteInt32("offset", (int)value.Offset.TotalMinutes);
                    bsonWriter.WriteEndDocument();
                    bsonWriter.WriteEndDocument();
                    break;

                case BsonType.String:
                    bsonWriter.WriteString(JsonConvert.ToString(value));
                    break;

                default:
                    var message = $"'{_representation}' is not a valid DateTimeOffset representation.";
                    throw new BsonSerializationException(message);
            }
        }

        /// <summary>
        /// Returns a serializer that has been reconfigured with the specified representation.
        /// </summary>
        /// <param name="representation">The representation.</param>
        /// <returns>The reconfigured serializer.</returns>
        public DateTimeOffsetSerializer WithRepresentation(BsonType representation)
        {
            return representation == _representation ? this : new DateTimeOffsetSerializer(representation);
        }

        // explicit interface implementations
        IBsonSerializer IRepresentationConfigurable.WithRepresentation(BsonType representation)
        {
            return WithRepresentation(representation);
        }
}