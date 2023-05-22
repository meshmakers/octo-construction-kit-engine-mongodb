using System.Text.Json;

namespace Meshmakers.Octo.Common.Shared;

public static class ObjectExtensions
{
    public static string Serialize(this object o)
    {
        // TODO: Move to commmon shared

        var serializerOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        return JsonSerializer.Serialize(o, serializerOptions);
    }
}