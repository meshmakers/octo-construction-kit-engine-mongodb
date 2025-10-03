
using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Geospatial.Geometry;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Entities;
using MongoDB.Driver;
using MongoDB.Driver.GeoJsonObjectModel;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

public class RtEntityMongoDataSourceMapper<TEntity> : IMongoDataSourceMapper<OctoObjectId, TEntity> where TEntity : RtEntity, new()
{
    public string CollectionNamePrefix => "RtEntity";

    public OctoObjectId GetId(TEntity document)
    {
        return document.RtId;
    }

    public UpdateDefinition<TEntity> ApplyUpdate(TEntity document)
    {
        List<UpdateDefinition<TEntity>> list = [];
        foreach (var attributeToApply in document.Attributes)
        {
            var value = attributeToApply.Value;
            if (value is Point p)
            {
               value = GeoJson.Point(GeoJson.Position(p.Coordinates.Longitude, p.Coordinates.Latitude));
            }

            list.Add(Builders<TEntity>.Update.Set("attributes." + attributeToApply.Key.ToCamelCase(), value));
        }

        if (document.RtChangedDateTime.HasValue)
        {
            list.Add(Builders<TEntity>.Update.Set("rtChangedDateTime", document.RtChangedDateTime.Value));
        }
        
        if (!string.IsNullOrWhiteSpace(document.RtWellKnownName))
        {
            list.Add(Builders<TEntity>.Update.Set("rtWellKnownName", document.RtWellKnownName));
        }

        if (document.RtState.HasValue)
        {
            list.Add(Builders<TEntity>.Update.Set("rtState", document.RtState.Value));
        }

        if (document.RtDeletedDateTime.HasValue)
        {
            list.Add(Builders<TEntity>.Update.Set("rtDeletedDateTime", document.RtDeletedDateTime.Value));
        }
        
        list.Add(Builders<TEntity>.Update.Inc("rtVersion", 1));

        return Builders<TEntity>.Update.Combine(list);
    }
}

public class CkModelMongoDataSourceMapper : IMongoDataSourceMapper<CkModelId, CkModel>
{
    public string CollectionNamePrefix => nameof(CkModel);

    public CkModelId GetId(CkModel document)
    {
        return document.Id;
    }

    public UpdateDefinition<CkModel> ApplyUpdate(CkModel document)
    {
        var update = Builders<CkModel>.Update;
        List<UpdateDefinition<CkModel>> list =
        [
            update.Set(p => p.ModelState, document.ModelState),
            update.Set(p => p.Dependencies, document.Dependencies)
        ];

        return update.Combine(list);
    }
}

public class CkTypeMongoDataSourceMapper : IMongoDataSourceMapper<CkId<CkTypeId>, CkType>
{
    public string CollectionNamePrefix => nameof(CkType);

    public CkId<CkTypeId> GetId(CkType document)
    {
        return document.CkTypeId;
    }

    public UpdateDefinition<CkType> ApplyUpdate(CkType document)
    {
        var update = Builders<CkType>.Update;
        List<UpdateDefinition<CkType>> list =
        [
            update.Set(p => p.Attributes, document.Attributes),
            update.Set(p => p.CkModelId, document.CkModelId),
            update.Set(p => p.EnableChangeStreamPreAndPostImages, document.EnableChangeStreamPreAndPostImages),
            update.Set(p => p.Indexes, document.Indexes),
            update.Set(p => p.IsAbstract, document.IsAbstract),
            update.Set(p => p.IsFinal, document.IsFinal)
        ];

        return update.Combine(list);
    }
}

public class CkRecordMongoDataSourceMapper : IMongoDataSourceMapper<CkId<CkRecordId>, CkRecord>
{
    public string CollectionNamePrefix => nameof(CkRecord);

    public CkId<CkRecordId> GetId(CkRecord document)
    {
        return document.CkRecordId;
    }

    public UpdateDefinition<CkRecord> ApplyUpdate(CkRecord document)
    {
        var update = Builders<CkRecord>.Update;
        List<UpdateDefinition<CkRecord>> list =
        [
            update.Set(p => p.Attributes, document.Attributes),
            update.Set(p => p.CkModelId, document.CkModelId),
            update.Set(p => p.IsAbstract, document.IsAbstract),
            update.Set(p => p.IsFinal, document.IsFinal)
        ];

        return update.Combine(list);
    }
}

public class CkEnumMongoDataSourceMapper : IMongoDataSourceMapper<CkId<CkEnumId>, CkEnum>
{
    public string CollectionNamePrefix => nameof(CkEnum);

    public CkId<CkEnumId> GetId(CkEnum document)
    {
        return document.CkEnumId;
    }

    public UpdateDefinition<CkEnum> ApplyUpdate(CkEnum document)
    {
        var update = Builders<CkEnum>.Update;
        List<UpdateDefinition<CkEnum>> list =
        [
            update.Set(p => p.CkModelId, document.CkModelId),
            update.Set(p => p.UseFlags, document.UseFlags),
            update.Set(p => p.IsExtensible, document.IsExtensible),
            update.Set(p => p.Values, document.Values)
        ];

        return update.Combine(list);
    }
}

public class CkAttributeMongoDataSourceMapper : IMongoDataSourceMapper<CkId<CkAttributeId>, CkAttribute>
{
    public string CollectionNamePrefix => nameof(CkAttribute);

    public CkId<CkAttributeId> GetId(CkAttribute document)
    {
        return document.CkAttributeId;
    }

    public UpdateDefinition<CkAttribute> ApplyUpdate(CkAttribute document)
    {
        var update = Builders<CkAttribute>.Update;
        List<UpdateDefinition<CkAttribute>> list =
        [
            update.Set(p => p.CkModelId, document.CkModelId),
            update.Set(p => p.AttributeValueType, document.AttributeValueType),
            update.Set(p => p.DefaultValues, document.DefaultValues),
            update.Set(p => p.ValueCkEnumId, document.ValueCkEnumId),
            update.Set(p => p.ValueCkRecordId, document.ValueCkRecordId),
            update.Set(p => p.Description, document.Description)
        ];

        return update.Combine(list);
    }
}

public class CkAssociationRoleMongoDataSourceMapper : IMongoDataSourceMapper<CkId<CkAssociationRoleId>, CkAssociationRole>
{
    public string CollectionNamePrefix => nameof(CkAssociationRole);

    public CkId<CkAssociationRoleId> GetId(CkAssociationRole document)
    {
        return document.RoleId;
    }

    public UpdateDefinition<CkAssociationRole> ApplyUpdate(CkAssociationRole document)
    {
        var update = Builders<CkAssociationRole>.Update;
        List<UpdateDefinition<CkAssociationRole>> list =
        [
            update.Set(p => p.CkModelId, document.CkModelId),
            update.Set(p => p.InboundName, document.InboundName),
            update.Set(p => p.OutboundName, document.OutboundName),
            update.Set(p => p.InboundMultiplicity, document.InboundMultiplicity),
            update.Set(p => p.OutboundMultiplicity, document.OutboundMultiplicity),
            update.Set(p => p.Attributes, document.Attributes)
        ];

        return update.Combine(list);
    }
}

public class CkTypeAssociationMongoDataSourceMapper : IMongoDataSourceMapper<OctoObjectId, CkTypeAssociation>
{
    public string CollectionNamePrefix => nameof(CkTypeAssociation);

    public OctoObjectId GetId(CkTypeAssociation document)
    {
        return document.AssociationId;
    }

    public UpdateDefinition<CkTypeAssociation> ApplyUpdate(CkTypeAssociation document)
    {
        var update = Builders<CkTypeAssociation>.Update;
        List<UpdateDefinition<CkTypeAssociation>> list =
        [
            update.Set(p => p.CkModelId, document.CkModelId),
            update.Set(p => p.RoleId, document.RoleId),
            update.Set(p => p.OriginCkTypeId, document.OriginCkTypeId),
            update.Set(p => p.TargetCkTypeId, document.TargetCkTypeId),
            update.Set(p => p.TargetCkAttributeIds, document.TargetCkAttributeIds)
        ];

        return update.Combine(list);
    }
}

public class CkTypeInheritanceMongoDataSourceMapper : IMongoDataSourceMapper<OctoObjectId, CkTypeInheritance>
{
    public string CollectionNamePrefix => nameof(CkTypeInheritance);

    public OctoObjectId GetId(CkTypeInheritance document)
    {
        return document.InheritanceId;
    }

    public UpdateDefinition<CkTypeInheritance> ApplyUpdate(CkTypeInheritance document)
    {
        var update = Builders<CkTypeInheritance>.Update;
        List<UpdateDefinition<CkTypeInheritance>> list =
        [
            update.Set(p => p.CkModelId, document.CkModelId),
            update.Set(p => p.BaseCkTypeId, document.BaseCkTypeId),
            update.Set(p => p.InheritorCkTypeId, document.InheritorCkTypeId)
        ];

        return update.Combine(list);
    }
}

public class CkRecordInheritanceMongoDataSourceMapper : IMongoDataSourceMapper<OctoObjectId, CkRecordInheritance>
{
    public string CollectionNamePrefix => nameof(CkRecordInheritance);

    public OctoObjectId GetId(CkRecordInheritance document)
    {
        return document.InheritanceId;
    }

    public UpdateDefinition<CkRecordInheritance> ApplyUpdate(CkRecordInheritance document)
    {
        var update = Builders<CkRecordInheritance>.Update;
        List<UpdateDefinition<CkRecordInheritance>> list =
        [
            update.Set(p => p.CkModelId, document.CkModelId),
            update.Set(p => p.BaseCkRecordId, document.BaseCkRecordId),
            update.Set(p => p.InheritorCkRecordId, document.InheritorCkRecordId)
        ];

        return update.Combine(list);
    }
}

public class RtAssociationMongoDataSourceMapper : IMongoDataSourceMapper<OctoObjectId, RtAssociation>
{
    public string CollectionNamePrefix => nameof(RtAssociation);

    public OctoObjectId GetId(RtAssociation document)
    {
        return document.AssociationId;
    }

    public UpdateDefinition<RtAssociation> ApplyUpdate(RtAssociation document)
    {
        var update = Builders<RtAssociation>.Update;
        List<UpdateDefinition<RtAssociation>> list =
        [
            update.Set(p => p.OriginRtId, document.OriginRtId),
            update.Set(p => p.OriginCkTypeId, document.OriginCkTypeId),
            update.Set(p => p.TargetRtId, document.TargetRtId),
            update.Set(p => p.TargetCkTypeId, document.TargetCkTypeId),
            update.Set(p => p.AssociationRoleId, document.AssociationRoleId)
        ];

        return update.Combine(list);
    }
}
