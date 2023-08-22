using System.Text.Json;
using Json.Schema;

namespace Meshmakers.Octo.SystematizedData.CkModel.Contracts.Serialization.Schema;

public static class CkSchema
{
    private const string SchemaPath = "Meshmakers.Octo.SystematizedData.CkModel.Contracts.Serialization.Schema.{0}.json";

    static CkSchema()
    {
        ElementsSchema = GetSchema(string.Format(SchemaPath, "ck-elements"));
        MetaSchema = GetSchema(string.Format(SchemaPath, "ck-meta"));
        CompiledModelSchema = GetSchema(string.Format(SchemaPath, "ck-compiled-model"));
        SchemaRegistry.Global.Register(ElementsSchema);
        SchemaRegistry.Global.Register(MetaSchema);
        SchemaRegistry.Global.Register(CompiledModelSchema);
        
        SchemaRegistry.Global.Register(GetSchema(string.Format(SchemaPath, "ck-element-attribute")));
        SchemaRegistry.Global.Register(GetSchema(string.Format(SchemaPath, "ck-element-type")));
        SchemaRegistry.Global.Register(GetSchema(string.Format(SchemaPath, "ck-element-associationRole")));
    }
    
    public static JsonSchema ElementsSchema { get; } 
    public static JsonSchema MetaSchema { get; } 
    public static JsonSchema CompiledModelSchema { get; } 
    
    private static JsonSchema GetSchema(string resourcesStreamPath)
    {
        var assembly = typeof(ICkSerializer).Assembly;
        var resourcesStream = assembly.GetManifestResourceStream(resourcesStreamPath);
        if (resourcesStream == null)
        {
            throw new ModelValidationException($"Resource with path '{resourcesStreamPath}' not found in assembly '{assembly.FullName}'.");
        }

        return JsonSerializer.Deserialize<JsonSchema>(resourcesStream) ??
               throw new ModelValidationException($"Could not deserialize schema '{resourcesStreamPath}'.");
    }
}