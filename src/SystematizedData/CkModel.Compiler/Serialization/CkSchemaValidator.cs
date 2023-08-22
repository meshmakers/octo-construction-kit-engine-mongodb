using System.Text.Json.Nodes;
using Json.Schema;
using Meshmakers.Octo.SystematizedData.CkModel.Compiler.Messages;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.Serialization;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.Serialization.Schema;
using Yaml2JsonNode;
using YamlDotNet.RepresentationModel;

namespace Meshmakers.Octo.SystematizedData.CkModel.Compiler.Serialization;

public class CkSchemaValidator : ICkSchemaValidator
{
    public bool ValidateElementsInJson(Stream stream, OperationResult operationResult)
    {
        return ValidateModelJson(stream, CkSchema.ElementsSchema, operationResult);
    }

    public bool ValidateMetaInJson(Stream stream, OperationResult operationResult)
    {
        return ValidateModelJson(stream, CkSchema.MetaSchema, operationResult);
    }

    public bool ValidateCompiledModelInJson(Stream stream, OperationResult operationResult)
    {
        return ValidateModelJson(stream, CkSchema.CompiledModelSchema, operationResult);
    }

    public bool ValidateElementsInYaml(Stream stream, OperationResult operationResult)
    {
        return ValidateModelYaml(stream, CkSchema.ElementsSchema, operationResult);
    }

    public bool ValidateMetaInYaml(Stream stream, OperationResult operationResult)
    {
        return ValidateModelYaml(stream, CkSchema.MetaSchema, operationResult);
    }

    public bool ValidateCompiledModelInYaml(Stream stream, OperationResult operationResult)
    {
        return ValidateModelYaml(stream, CkSchema.CompiledModelSchema, operationResult);
    }

    private static bool ValidateModelJson(Stream stream, JsonSchema schema, OperationResult operationResult)
    {
        var json = JsonNode.Parse(stream);

        var evaluationResults = schema.Evaluate(json, new EvaluationOptions { OutputFormat = OutputFormat.List});
        return ValidateEvaluationResults(operationResult, evaluationResults);
    }

    private static bool ValidateModelYaml(Stream stream, JsonSchema schema, OperationResult operationResult)
    {
        using var memoryStream = new MemoryStream();
        stream.CopyTo(memoryStream);
        stream.Position = 0;
        memoryStream.Seek(0, SeekOrigin.Begin);
        
        using var streamReader = new StreamReader(memoryStream);
        var yamlStream = new YamlStream();
        yamlStream.Load(streamReader);
        var singleNode = yamlStream.Documents[0].ToJsonNode();

        var evaluationResults = schema.Evaluate(singleNode, new EvaluationOptions { OutputFormat = OutputFormat.List });
        return ValidateEvaluationResults(operationResult, evaluationResults);
    }

    private static bool ValidateEvaluationResults(OperationResult operationResult, EvaluationResults evaluationResults)
    {
        if (!evaluationResults.IsValid)
        {
            foreach (var evaluationResult in evaluationResults.Details.Where(x => x.HasErrors))
            {
                var path = evaluationResult.InstanceLocation.ToString();
                var errorMessages = string.Join(", ", evaluationResults.Errors?.Values ?? Enumerable.Empty<string>());
                operationResult.AddMessage(MessageCodes.SchemaValidationError($"{path}: {errorMessages}"));
            }
        }

        return evaluationResults.IsValid;
    }
}