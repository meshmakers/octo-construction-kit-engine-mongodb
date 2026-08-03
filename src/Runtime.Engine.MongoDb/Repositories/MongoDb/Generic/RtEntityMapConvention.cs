using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

/// <summary>
///     Defines the map convention for RtEntity.
/// </summary>
/// <remarks>
///     This convention is used to prevent the serialization of properties of
///     derived classes from RtEntity. This is necessary because the properties are
///     stored as attributes in the database, and the serialization of the properties
///     results in duplicate values.
///     The class map is used to register a custom creator delegate for the RtEntity class to
///     enable polymorphic deserialization.
/// </remarks>
internal class RtEntityMapConvention(ICkClassMappingService ckClassMappingService)
    : IMemberMapConvention, IClassMapConvention
{
    public void Apply(BsonClassMap classMap)
    {
        // Capture the mapped class as the fallback: a CK type without a generated CLR class
        // (e.g. a customer model's Configuration-derived type) must still deserialize to the
        // NOMINAL type of the query (RtConfiguration, ...), not to bare RtEntity — otherwise a
        // typed read such as the pipeline Uses-configuration lookup fails with an InvalidCastException.
        var fallbackType = classMap.ClassType.IsAbstract ? typeof(RtEntity) : classMap.ClassType;
        Delegate @delegate = (RtCkId<CkTypeId> ckTypeId, OctoObjectId rtId) =>
            CreateInstance(fallbackType, ckTypeId, rtId);
        var mapCreator = classMap.MapCreator(@delegate);
        mapCreator.SetArguments([nameof(RtEntity.CkTypeId), nameof(RtEntity.RtId)]);
    }

    public void Apply(BsonMemberMap memberMap)
    {
        memberMap.SetShouldSerializeMethod(_ => false);
    }

    public string Name => "RtEntityMapConvention";

    private RtEntity CreateInstance(Type fallbackType, RtCkId<CkTypeId> ckTypeId, OctoObjectId rtId)
    {
        var type = ckClassMappingService.GetCkTypeClass(ckTypeId) ?? fallbackType;

        var rtEntity = (RtEntity?)Activator.CreateInstance(type) ?? new RtEntity();
        rtEntity.CkTypeId = ckTypeId;
        rtEntity.RtId = rtId;

        return rtEntity;
    }
}
