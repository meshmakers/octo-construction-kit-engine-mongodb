using System.Text.Json;
using System.Text.Json.Serialization;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Common.Shared.Exchange;

public static class CkSerializer
{
    public static async Task SerializeAsync(StreamWriter streamWriter, CkModelRoot model)
    {
        var options = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        await JsonSerializer.SerializeAsync(streamWriter.BaseStream, model, options);
    }

    public static async Task<CkModelRoot?> DeserializeAsync(string s)
    {
        byte[] byteArray = System.Text.Encoding.UTF8.GetBytes(s);
        using var memStream = new MemoryStream(byteArray);
        using var streamReader = new StreamReader(memStream);   
        return await DeserializeAsync(streamReader);
    }

    public static async Task<CkModelRoot?> DeserializeAsync(StreamReader textReader)
    {
        var options = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        return await JsonSerializer.DeserializeAsync<CkModelRoot?>(textReader.BaseStream, options);
    }
}
