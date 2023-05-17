using System.Text.Json;

namespace Meshmakers.Octo.Communication.Plugs.Contracts.Configuration;

public class MappingConfiguration
{
    public string Name { get; set; } = null!;
    
    public string Configuration { get; set; } = null!; 
    
    public T Deserialize<T>()
    {
        return JsonSerializer.Deserialize<T>(Configuration)!;
    }
    
    public void Serialize<T>(T configuration)
    {
        Configuration = JsonSerializer.Serialize(configuration);
    }
}