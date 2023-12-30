using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

/// <summary>
///     Defines the serialization convention for RtEntity.
/// </summary>
/// <remarks>
///     This convention is used to prevent the serialization of properties of
///     derived classes from RtEntity. This is necessary because the properties are
///     stored as attributes in the database, and the serialization of the properties
///     results in duplicate values.
/// </remarks>
internal class RtEntitySerializationConvention : IMemberMapConvention
{
    public void Apply(BsonMemberMap memberMap)
    {
        memberMap.SetShouldSerializeMethod(o => false);
    }

    public string Name => "RtEntitySerializationConvention";
}