using Meshmakers.Octo.SystematizedData.Persistence.DataAccess.Internal;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

public class CkEntityQuery : SingleOriginQuery<CkType>
{
    public CkEntityQuery(IDatabaseContext databaseContext) : base(databaseContext.CkTypesInternal)
    {
    }
}
