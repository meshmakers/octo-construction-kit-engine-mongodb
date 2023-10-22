using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess.Internal;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

public class CkAttributeQuery : SingleOriginQuery<CkId<CkAttributeId>, CkAttribute>
{
    public CkAttributeQuery(IDatabaseContext databaseContext) : base(databaseContext.CkAttributes)
    {
    }
}
