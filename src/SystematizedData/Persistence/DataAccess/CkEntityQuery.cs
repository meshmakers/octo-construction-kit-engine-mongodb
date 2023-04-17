using Meshmakers.Octo.Backend.Persistence.DataAccess.Internal;
using Meshmakers.Octo.Backend.Persistence.DatabaseEntities;

namespace Meshmakers.Octo.Backend.Persistence.DataAccess;

public class CkEntityQuery : SingleOriginQuery<CkEntity>
{
    public CkEntityQuery(IDatabaseContext databaseContext) : base(databaseContext.CkEntities)
    {
    }
}
