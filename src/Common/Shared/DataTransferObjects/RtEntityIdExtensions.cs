using System;

namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

public static class RtEntityIdExtensions
{
    public static RtEntityId ToRtEntityId(this RtEntityDto rtEntity)
    {
        if (!rtEntity.RtId.HasValue)
        {
            throw new InvalidOperationException("RtEntity does not defines an valid RtId");
        }

        if (rtEntity.CkId == null)
        {
            throw new InvalidOperationException("RtEntity does not defines an valid CkId");
        }

        return new RtEntityId(rtEntity.CkId, rtEntity.RtId.Value);
    }
}
