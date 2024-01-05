using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository.Entities;
using Meshmakers.Octo.Runtime.Contracts.Repositories;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

public interface ICkMongoDbRepositoryDataSource
{
    IDataSourceCollection<CkModelId, CkModel> CkModels { get; }
    IMongoDbDataSourceCollection<CkId<CkTypeId>, CkType> CkTypes { get; }
    IMongoDbDataSourceCollection<CkId<CkAttributeId>, CkAttribute> CkAttributes { get; }
    IMongoDbDataSourceCollection<CkId<CkEnumId>, CkEnum> CkEnums { get; }
    IMongoDbDataSourceCollection<CkId<CkRecordId>, CkRecord> CkRecords { get; }
    IDataSourceCollection<CkId<CkAssociationRoleId>, CkAssociationRole> CkAssociationRoles { get; }
    IMongoDbDataSourceCollection<OctoObjectId, CkTypeAssociation> CkTypeAssociations { get; }
    IDataSourceCollection<OctoObjectId, CkTypeInheritance> CkTypeInheritances { get; }
    IDataSourceCollection<OctoObjectId, CkRecordInheritance> CkRecordInheritances { get; }

    Task UpdateCollectionsAsync(IOctoSession session);
    Task UpdateIndexAsync(IOctoSession session);
}