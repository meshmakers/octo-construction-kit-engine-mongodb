using System.Globalization;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Serialization;

/// <summary>
/// BSON serializer for <see cref="TimeSpan"/> that round-trips via BSON <c>Int64</c> (ticks).
/// Required because CK attribute values are stored in a <c>Dictionary&lt;string, object?&gt;</c>:
/// without a registered serializer the driver routes the value through the global
/// <c>OctoObjectSerializer</c>, which writes TimeSpan as a string and breaks the read path
/// (<c>Convert.ChangeType</c> in <c>RtTypeWithAttributes.GetAttributeValueOrDefault</c> does not
/// support <c>String → TimeSpan</c>).
/// </summary>
/// <remarks>
/// Read tolerates legacy data shapes that may already be in the wild:
/// <list type="bullet">
/// <item><description><c>Int64</c> / <c>Int32</c> — interpreted as ticks (the canonical form).</description></item>
/// <item><description><c>Double</c> — interpreted as ticks (truncated).</description></item>
/// <item><description><c>String</c> — parsed via <see cref="TimeSpan.TryParse(string, IFormatProvider, out TimeSpan)"/> with invariant culture; ISO-8601 ('PT15M') is also recognised via a fallback to <see cref="System.Xml.XmlConvert.ToTimeSpan"/>.</description></item>
/// </list>
/// Write always emits <c>Int64</c> ticks.
/// </remarks>
internal class TimeSpanSerializer : StructSerializerBase<TimeSpan>
{
    public override TimeSpan Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        var reader = context.Reader;
        var bsonType = reader.GetCurrentBsonType();
        switch (bsonType)
        {
            case BsonType.Int64:
                return TimeSpan.FromTicks(reader.ReadInt64());
            case BsonType.Int32:
                return TimeSpan.FromTicks(reader.ReadInt32());
            case BsonType.Double:
                return TimeSpan.FromTicks((long)reader.ReadDouble());
            case BsonType.String:
                var raw = reader.ReadString();
                // A bare-integer string is the canonical ticks form (matching the Int64/Int32 cases
                // above), NOT a .NET TimeSpan literal — TimeSpan.Parse reads "9000000000" as
                // 9-billion days and overflows. This shape is produced by the ImportRt export/import
                // JSON round-trip (AB#4259). Check before the .NET / ISO-8601 parse.
                if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticksFromString))
                {
                    return TimeSpan.FromTicks(ticksFromString);
                }

                if (TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out var dotnetParsed))
                {
                    return dotnetParsed;
                }

                try
                {
                    return System.Xml.XmlConvert.ToTimeSpan(raw);
                }
                catch (FormatException ex)
                {
                    throw new FormatException(
                        $"Cannot deserialize TimeSpan from string '{raw}'. Expected .NET TimeSpan format (e.g. '00:15:00') or ISO-8601 duration (e.g. 'PT15M').",
                        ex);
                }
            case BsonType.Null:
                reader.ReadNull();
                return TimeSpan.Zero;
            default:
                throw CreateCannotDeserializeFromBsonTypeException(bsonType);
        }
    }

    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, TimeSpan value)
    {
        context.Writer.WriteInt64(value.Ticks);
    }

    public override bool Equals(object? obj)
    {
        if (ReferenceEquals(obj, null)) return false;
        if (ReferenceEquals(this, obj)) return true;
        return obj is TimeSpanSerializer;
    }

    public override int GetHashCode() => 0;
}
