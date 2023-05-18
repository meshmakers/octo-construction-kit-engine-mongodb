using System.Text.Json;

namespace Meshmakers.Octo.Communication.Plugs.Contracts.Configuration;

public class MappingConfiguration
{
    public string Name { get; set; } = null!;
    
    public string Configuration { get; set; } = null!; 
    
    public T Deserialize<T>()
    {
        var serializerOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        return JsonSerializer.Deserialize<T>(Configuration, serializerOptions)!;
    }
    
    public void Serialize<T>(T configuration)
    {
        var serializerOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        Configuration = JsonSerializer.Serialize(configuration, serializerOptions);
    }
}