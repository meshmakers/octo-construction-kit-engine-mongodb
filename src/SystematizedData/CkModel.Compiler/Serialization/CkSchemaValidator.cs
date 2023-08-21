using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Meshmakers.Octo.SystematizedData.CkModel.Compiler.Messages;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.Serialization;
using Yaml2JsonNode;
using YamlDotNet.RepresentationModel;

namespace Meshmakers.Octo.SystematizedData.CkModel.Compiler.Serialization;

public class CkSchemaValidator
{
    private const string SchemaPath = "Meshmakers.Octo.SystematizedData.CkModel.Contracts.Serialization.Schema.{0}.json";
    private static bool _loadSchema;

    // ReSharper disable once ConvertConstructorToMemberInitializers
    public CkSchemaValidator()
    {
        if (!_loadSchema)
        {
            LoadSchema();
            _loadSchema = true;
        }
    }

    public bool ValidateElementsInJson(Stream stream, CompilerResult compilerResult)
    {
        var json = JsonNode.Parse(stream);

        var schema = new JsonSchemaBuilder()
            .Ref(CompilerStatics.CkElementsSchemaUri)
            .Build();

        var evaluationResults = schema.Evaluate(json, new EvaluationOptions { OutputFormat = OutputFormat.List});
        return ValidateEvaluationResults(compilerResult, evaluationResults);

    }

    public bool ValidateElementsInYaml(TextReader stream, CompilerResult compilerResult)
    {
        var yamlStream = new YamlStream();
        yamlStream.Load(stream);
        var singleNode = yamlStream.Documents[0].ToJsonNode();

        var schema = (JsonSchema)SchemaRegistry.Global.Get(new Uri(CompilerStatics.CkElementsSchemaUri))!;

        var evaluationResults = schema.Evaluate(singleNode, new EvaluationOptions { OutputFormat = OutputFormat.List});
        return ValidateEvaluationResults(compilerResult, evaluationResults);
    }

    private static bool ValidateEvaluationResults(CompilerResult compilerResult, EvaluationResults evaluationResults)
    {
        if (!evaluationResults.IsValid)
        {
            foreach (var evaluationResult in evaluationResults.Details.Where(x => x.HasErrors))
            {
                var path = evaluationResult.InstanceLocation.ToString();
                var errorMessages = string.Join(", ", evaluationResults.Errors?.Values ?? Enumerable.Empty<string>());
                compilerResult.AddMessage(MessageCodes.SchemaValidationError($"{path}: {errorMessages}"));
            }
        }

        return evaluationResults.IsValid;
    }

    private static void LoadSchema()
    {
        SchemaRegistry.Global.Register(GetSchema(string.Format(SchemaPath, "ck-elements")));
        SchemaRegistry.Global.Register(GetSchema(string.Format(SchemaPath, "ck-meta")));
        SchemaRegistry.Global.Register(GetSchema(string.Format(SchemaPath, "ck-compiled-model")));
        SchemaRegistry.Global.Register(GetSchema(string.Format(SchemaPath, "ck-element-attribute")));
        SchemaRegistry.Global.Register(GetSchema(string.Format(SchemaPath, "ck-element-attribute")));
        SchemaRegistry.Global.Register(GetSchema(string.Format(SchemaPath, "ck-element-type")));
        SchemaRegistry.Global.Register(GetSchema(string.Format(SchemaPath, "ck-element-associationRole")));
    }

    internal static JsonSchema GetSchema(string resourcesStreamPath)
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