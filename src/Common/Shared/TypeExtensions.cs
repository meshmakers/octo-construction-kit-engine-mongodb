namespace Meshmakers.Octo.Common.Shared;

public static class TypeExtensions
{
    public static Type GetMostInnerBaseType(this Type type)
    {
        while (type.BaseType != null && !type.BaseType.IsInterface && type.BaseType != typeof(object)) type = type.BaseType;
        return type;
    }
}