using MongoDB.Bson;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Serialization;

public class OctoSerializationException : Exception
{
    public OctoSerializationException()
    {
    }

    public OctoSerializationException(string message) : base(message)
    {
    }

    public OctoSerializationException(string message, Exception inner) : base(message, inner)
    {
    }

    public static Exception MemberNotFound(string memberName)
    {
        return new OctoSerializationException($"Member '{memberName}' not found.");
    }

    public static Exception InvalidRecordId()
    {
        return new OctoSerializationException("Invalid record id.");
    }

    public static Exception UnsupportedBsonType(BsonType readerCurrentBsonType)
    {
        return new OctoSerializationException($"Unsupported BSON type: {readerCurrentBsonType}");
    }

    public static Exception UnsupportedType(Type type)
    {
        return new OctoSerializationException($"Unsupported type: {type}");
    }
}

