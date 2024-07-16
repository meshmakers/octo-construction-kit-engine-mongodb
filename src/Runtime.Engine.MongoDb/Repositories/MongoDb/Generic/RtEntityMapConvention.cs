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
    public void Apply(BsonMemberMap memberMap)
    {
        memberMap.SetShouldSerializeMethod(_ => false);
    }
    
    public void Apply(BsonClassMap classMap)
    {
        Delegate @delegate = CreateInstance;
        var mapCreator = classMap.MapCreator(@delegate);
        mapCreator.SetArguments(new[] { nameof(RtEntity.CkTypeId), nameof(RtEntity.RtId) });
    }

    public string Name => "RtEntityMapConvention";

    private RtEntity CreateInstance(CkId<CkTypeId> ckTypeId, OctoObjectId rtId)
    {
        var type = ckClassMappingService.GetCkTypeClass(ckTypeId);

        var rtEntity = (type == null ? new RtEntity() : (RtEntity?)Activator.CreateInstance(type)) ?? new RtEntity();
        rtEntity.CkTypeId = ckTypeId;
        rtEntity.RtId = rtId;

        return rtEntity;
    }
}

