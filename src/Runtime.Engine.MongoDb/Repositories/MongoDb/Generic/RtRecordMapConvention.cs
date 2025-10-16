using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

/// <summary>
///     Defines the serialization convention for RtRecord.
/// </summary>
/// <remarks>
///     This convention is used to prevent the serialization of properties of
///     derived classes from RtRecord. This is necessary because the properties are
///     stored as attributes in the database, and the serialization of the properties
///     results in duplicate values.
///     The class map is used to register a custom creator delegate for the RtRecord class to
///     enable polymorphic deserialization.
/// </remarks>
internal class RtRecordMapConvention : IMemberMapConvention, IClassMapConvention
{
    private readonly ICkClassMappingService _ckClassMappingService;

    public RtRecordMapConvention(ICkClassMappingService ckClassMappingService)
    {
        _ckClassMappingService = ckClassMappingService;
    }

    public void Apply(BsonClassMap classMap)
    {
        Delegate @delegate = CreateInstance;
        var creatorMap = classMap.MapCreator(@delegate);
        creatorMap.SetArguments(new[] { nameof(RtRecord.CkRecordId) });
    }

    public void Apply(BsonMemberMap memberMap)
    {
        memberMap.SetShouldSerializeMethod(o => false);
    }

    public string Name => "RtRecordMapConvention";

    private RtRecord CreateInstance(RtCkId<CkRecordId> rtCkRecordId)
    {
        var type = _ckClassMappingService.GetCkRecordClass(rtCkRecordId);

        var rtRecord = (type == null ? new RtRecord() : (RtRecord?)Activator.CreateInstance(type)) ?? new RtRecord();
        rtRecord.CkRecordId = rtCkRecordId;

        return rtRecord;
    }
}
