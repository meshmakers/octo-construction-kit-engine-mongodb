using System.Text.Json;

namespace Meshmakers.Octo.Common.Shared;

public static class StringExtensions
{
    public static string MakeKey(this string s)
    {
        return s.Trim().ToLower();
    }

    /// <summary>
    ///     Creates a GraphQL name of the given string
    /// </summary>
    /// <param name="s"></param>
    /// <returns></returns>
    public static string GetGraphQlName(this string s)
    {
        return s.Replace(".", "");
    }

    /// <summary>
    /// Deserializes a string into a given type
    /// </summary>
    /// <param name="s"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static T Deserialize<T>(this string s)
    {
        // TODO: Move to commmon shared
        var serializerOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        return JsonSerializer.Deserialize<T>(s, serializerOptions)!;
    }
    

}
