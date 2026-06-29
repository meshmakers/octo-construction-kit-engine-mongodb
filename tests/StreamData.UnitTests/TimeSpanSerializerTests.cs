using System;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Serialization;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

/// <summary>
/// AB#4259: <see cref="TimeSpanSerializer"/> writes ticks as BSON Int64 but must read back the
/// legacy / import-corrupted shapes that may already be in the wild — in particular the
/// bare-integer ticks *string* (<c>"9000000000"</c>) produced by the ImportRt export/import JSON
/// round-trip. <see cref="TimeSpan.Parse(string)"/> would read that as 9-billion days and overflow.
/// </summary>
public class TimeSpanSerializerTests
{
    private static TimeSpan Deserialize(BsonValue value)
    {
        var doc = new BsonDocument("v", value);
        using var reader = new BsonDocumentReader(doc);
        reader.ReadStartDocument();
        reader.ReadName("v");
        var ctx = BsonDeserializationContext.CreateRoot(reader);
        return new TimeSpanSerializer().Deserialize(ctx, default);
    }

    [Fact]
    public void Deserialize_Int64Ticks_ReturnsTicks()
    {
        Assert.Equal(TimeSpan.FromMinutes(15), Deserialize(new BsonInt64(9000000000L)));
    }

    [Fact]
    public void Deserialize_BareTicksString_ReturnsTicks()
    {
        Assert.Equal(TimeSpan.FromMinutes(15), Deserialize(new BsonString("9000000000")));
    }

    [Fact]
    public void Deserialize_DotNetString_Parsed()
    {
        Assert.Equal(TimeSpan.FromMinutes(15), Deserialize(new BsonString("00:15:00")));
    }

    [Fact]
    public void Deserialize_Iso8601String_Parsed()
    {
        Assert.Equal(TimeSpan.FromMinutes(15), Deserialize(new BsonString("PT15M")));
    }

    [Fact]
    public void Deserialize_GarbageString_Throws()
    {
        Assert.Throws<FormatException>(() => Deserialize(new BsonString("not-a-duration")));
    }

    [Fact]
    public void RoundTrip_SerializesAsInt64Ticks()
    {
        var doc = new BsonDocument();
        using (var writer = new BsonDocumentWriter(doc))
        {
            writer.WriteStartDocument();
            writer.WriteName("v");
            var ctx = BsonSerializationContext.CreateRoot(writer);
            new TimeSpanSerializer().Serialize(ctx, default, TimeSpan.FromMinutes(15));
            writer.WriteEndDocument();
        }

        Assert.Equal(BsonType.Int64, doc["v"].BsonType);
        Assert.Equal(9000000000L, doc["v"].AsInt64);
    }
}
