using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

namespace Meshmakers.Octo.SystematizedData.Persistence;

public class EntityUpdateInfo
{
    public EntityUpdateInfo(RtEntity rtEntity, EntityModOptions modOption)
    {
        RtEntity = rtEntity;
        ModOption = modOption;
    }

    public RtEntity RtEntity { get; }

    public EntityModOptions ModOption { get; }
}
