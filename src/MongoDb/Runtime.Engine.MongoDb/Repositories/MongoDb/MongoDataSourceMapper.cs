using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository.Entities;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using MongoDB.Driver;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

public class RtEntityMongoDataSourceMapper<TEntity> : IMongoDataSourceMapper<OctoObjectId, TEntity> where TEntity : RtEntity, new()
{
    public OctoObjectId GetId(TEntity document)
    {
        return document.RtId;
    }

    public UpdateDefinition<TEntity> ApplyUpdate(TEntity document)
    {
        List<UpdateDefinition<TEntity>> list = new();
        foreach (var attributeToApply in document.Attributes)
            list.Add(Builders<TEntity>.Update.Set("attributes." + attributeToApply.Key, attributeToApply.Value));

        return Builders<TEntity>.Update.Combine(list);
    }
}

public class CkModelMongoDataSourceMapper : IMongoDataSourceMapper<CkModelId, CkModel>
{
    public CkModelId GetId(CkModel document)
    {
        return document.Id;
    }

    public UpdateDefinition<CkModel> ApplyUpdate(CkModel document)
    {
        var update = Builders<CkModel>.Update;
        List<UpdateDefinition<CkModel>> list = new()
        {
            update.Set(p => p.ScopeId, document.ScopeId),
            update.Set(p => p.Dependencies, document.Dependencies)
        };

        return update.Combine(list);
    }
}

public class CkTypeMongoDataSourceMapper : IMongoDataSourceMapper<CkId<CkTypeId>, CkType>
{
    public CkId<CkTypeId> GetId(CkType document)
    {
        return document.CkTypeId;
    }

    public UpdateDefinition<CkType> ApplyUpdate(CkType document)
    {
        var update = Builders<CkType>.Update;
        List<UpdateDefinition<CkType>> list = new()
        {
            update.Set(p => p.Attributes, document.Attributes),
            update.Set(p => p.CkModelId, document.CkModelId),
            update.Set(p => p.EnableChangeStreamPreAndPostImages, document.EnableChangeStreamPreAndPostImages),
            update.Set(p => p.Indexes, document.Indexes),
            update.Set(p => p.IsAbstract, document.IsAbstract),
            update.Set(p => p.IsFinal, document.IsFinal)
        };

        return update.Combine(list);
    }
}

public class CkRecordMongoDataSourceMapper : IMongoDataSourceMapper<CkId<CkRecordId>, CkRecord>
{
    public CkId<CkRecordId> GetId(CkRecord document)
    {
        return document.CkRecordId;
    }

    public UpdateDefinition<CkRecord> ApplyUpdate(CkRecord document)
    {
        var update = Builders<CkRecord>.Update;
        List<UpdateDefinition<CkRecord>> list = new()
        {
            update.Set(p => p.Attributes, document.Attributes),
            update.Set(p => p.CkModelId, document.CkModelId),
            update.Set(p => p.IsAbstract, document.IsAbstract),
            update.Set(p => p.IsFinal, document.IsFinal)
        };

        return update.Combine(list);
    }
}

public class CkEnumMongoDataSourceMapper : IMongoDataSourceMapper<CkId<CkEnumId>, CkEnum>
{
    public CkId<CkEnumId> GetId(CkEnum document)
    {
        return document.CkEnumId;
    }

    public UpdateDefinition<CkEnum> ApplyUpdate(CkEnum document)
    {
        var update = Builders<CkEnum>.Update;
        List<UpdateDefinition<CkEnum>> list = new()
        {
            update.Set(p => p.CkModelId, document.CkModelId),
            update.Set(p => p.UseFlags, document.UseFlags),
            update.Set(p => p.Values, document.Values)
        };

        return update.Combine(list);
    }
}

public class CkAttributeMongoDataSourceMapper : IMongoDataSourceMapper<CkId<CkAttributeId>, CkAttribute>
{
    public CkId<CkAttributeId> GetId(CkAttribute document)
    {
        return document.AttributeId;
    }

    public UpdateDefinition<CkAttribute> ApplyUpdate(CkAttribute document)
    {
        var update = Builders<CkAttribute>.Update;
        List<UpdateDefinition<CkAttribute>> list = new()
        {
            update.Set(p => p.CkModelId, document.CkModelId),
            update.Set(p => p.AttributeValueType, document.AttributeValueType),
            update.Set(p => p.DefaultValues, document.DefaultValues),
            update.Set(p => p.ValueCkEnumId, document.ValueCkEnumId),
            update.Set(p => p.ValueCkRecordId, document.ValueCkRecordId),
            update.Set(p => p.Description, document.Description)
        };

        return update.Combine(list);
    }
}

public class CkAssociationRoleMongoDataSourceMapper : IMongoDataSourceMapper<CkId<CkAssociationRoleId>, CkAssociationRole>
{
    public CkId<CkAssociationRoleId> GetId(CkAssociationRole document)
    {
        return document.RoleId;
    }

    public UpdateDefinition<CkAssociationRole> ApplyUpdate(CkAssociationRole document)
    {
        var update = Builders<CkAssociationRole>.Update;
        List<UpdateDefinition<CkAssociationRole>> list = new()
        {
            update.Set(p => p.CkModelId, document.CkModelId),
            update.Set(p => p.InboundName, document.InboundName),
            update.Set(p => p.OutboundName, document.OutboundName),
            update.Set(p => p.InboundMultiplicity, document.InboundMultiplicity),
            update.Set(p => p.OutboundMultiplicity, document.OutboundMultiplicity),
            update.Set(p => p.Attributes, document.Attributes)
        };

        return update.Combine(list);
    }
}

public class CkTypeAssociationMongoDataSourceMapper : IMongoDataSourceMapper<OctoObjectId, CkTypeAssociation>
{
    public OctoObjectId GetId(CkTypeAssociation document)
    {
        return document.AssociationId;
    }

    public UpdateDefinition<CkTypeAssociation> ApplyUpdate(CkTypeAssociation document)
    {
        var update = Builders<CkTypeAssociation>.Update;
        List<UpdateDefinition<CkTypeAssociation>> list = new()
        {
            update.Set(p => p.CkModelId, document.CkModelId),
            update.Set(p => p.RoleId, document.RoleId),
            update.Set(p => p.OriginCkTypeId, document.OriginCkTypeId),
            update.Set(p => p.TargetCkTypeId, document.TargetCkTypeId),
            update.Set(p => p.TargetAttributes, document.TargetAttributes)
        };

        return update.Combine(list);
    }
}

public class CkTypeInheritanceMongoDataSourceMapper : IMongoDataSourceMapper<OctoObjectId, CkTypeInheritance>
{
    public OctoObjectId GetId(CkTypeInheritance document)
    {
        return document.InheritanceId;
    }

    public UpdateDefinition<CkTypeInheritance> ApplyUpdate(CkTypeInheritance document)
    {
        var update = Builders<CkTypeInheritance>.Update;
        List<UpdateDefinition<CkTypeInheritance>> list = new()
        {
            update.Set(p => p.CkModelId, document.CkModelId),
            update.Set(p => p.BaseCkTypeId, document.BaseCkTypeId),
            update.Set(p => p.InheritorCkTypeId, document.InheritorCkTypeId)
        };

        return update.Combine(list);
    }
}

public class CkRecordInheritanceMongoDataSourceMapper : IMongoDataSourceMapper<OctoObjectId, CkRecordInheritance>
{
    public OctoObjectId GetId(CkRecordInheritance document)
    {
        return document.InheritanceId;
    }

    public UpdateDefinition<CkRecordInheritance> ApplyUpdate(CkRecordInheritance document)
    {
        var update = Builders<CkRecordInheritance>.Update;
        List<UpdateDefinition<CkRecordInheritance>> list = new()
        {
            update.Set(p => p.CkModelId, document.CkModelId),
            update.Set(p => p.BaseCkRecordId, document.BaseCkRecordId),
            update.Set(p => p.InheritorCkRecordId, document.InheritorCkRecordId)
        };

        return update.Combine(list);
    }
}

public class RtAssociationMongoDataSourceMapper : IMongoDataSourceMapper<OctoObjectId, RtAssociation>
{
    public OctoObjectId GetId(RtAssociation document)
    {
        return document.AssociationId;
    }

    public UpdateDefinition<RtAssociation> ApplyUpdate(RtAssociation document)
    {
        var update = Builders<RtAssociation>.Update;
        List<UpdateDefinition<RtAssociation>> list = new()
        {
            update.Set(p => p.OriginRtId, document.OriginRtId),
            update.Set(p => p.OriginCkTypeId, document.OriginCkTypeId),
            update.Set(p => p.TargetRtId, document.TargetRtId),
            update.Set(p => p.TargetCkTypeId, document.TargetCkTypeId),
            update.Set(p => p.AssociationRoleId, document.AssociationRoleId)
        };

        return update.Combine(list);
    }
}