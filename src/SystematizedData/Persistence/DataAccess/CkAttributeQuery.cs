using Meshmakers.Octo.Backend.Persistence.DataAccess.Internal;
using Meshmakers.Octo.Backend.Persistence.DatabaseEntities;

namespace Meshmakers.Octo.Backend.Persistence.DataAccess;

public class CkAttributeQuery : SingleOriginQuery<CkAttribute>
{
    public CkAttributeQuery(IDatabaseContext databaseContext) : base(databaseContext.CkAttributes)
    {
    }
}
