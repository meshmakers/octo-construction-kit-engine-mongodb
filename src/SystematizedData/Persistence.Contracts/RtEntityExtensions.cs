using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence;

public static class RtEntityExtensions
{
    public static CkId<CkTypeId> GetCkId<TEntity>(this TEntity rtEntity)
        where TEntity : RtEntity
    {
        if (!string.IsNullOrWhiteSpace(rtEntity.CkId.Key.TypeId))
        {
            return rtEntity.CkId;
        }

        return GetCkId(rtEntity.GetType());
    }

    public static CkId<CkTypeId> GetCkId<TEntity>()
        where TEntity : RtEntity
    {
        return GetCkId(typeof(TEntity));
    }

    private static CkId<CkTypeId> GetCkId(Type type)
    {
        var customAttribute = Attribute.GetCustomAttribute(type, typeof(CkIdAttribute));
        if (customAttribute == null)
        {
            throw new InvalidCkIdException(
                $"Type '{type}' does not define attribute '{typeof(CkIdAttribute)}'");
        }

        var ckIdAttribute = (CkIdAttribute)customAttribute;
        return ckIdAttribute.CkId;
    }
}