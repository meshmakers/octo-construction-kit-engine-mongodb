using System;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

namespace Meshmakers.Octo.SystematizedData.Persistence;

internal static class RtEntityExtensions
{
    public static string GetCkId<TEntity>(this TEntity rtEntity)
        where TEntity : RtEntity
    {
        if (!string.IsNullOrWhiteSpace(rtEntity.CkId))
        {
            return rtEntity.CkId;
        }

        return GetCkId(rtEntity.GetType());
    }

    public static string GetCkId<TEntity>()
        where TEntity : RtEntity
    {
        return GetCkId(typeof(TEntity));
    }

    private static string GetCkId(Type type)
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