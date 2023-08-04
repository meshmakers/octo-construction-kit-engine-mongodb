namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

public static class RtEntityIdExtensions
{
    public static RtEntityId ToRtEntityId(this RtEntityDto rtEntity)
    {
        return new RtEntityId(rtEntity.CkId, rtEntity.RtId);
    }
}