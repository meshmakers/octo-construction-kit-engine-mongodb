using Meshmakers.Octo.Backend.Persistence.DatabaseEntities;
using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.Backend.Persistence;

public static class RtEntityIdExtensions
{
    public static RtEntityId ToRtEntityId(this RtEntity rtEntity)
    {
        return new RtEntityId(rtEntity.CkId, rtEntity.RtId.ToOctoObjectId());
    }
}
