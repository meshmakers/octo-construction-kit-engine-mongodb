using Meshmakers.Octo.Backend.Persistence.DatabaseEntities;

namespace Meshmakers.Octo.Backend.Persistence;

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
