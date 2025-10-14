using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;

using Meshmakers.Octo.Runtime.Engine.MongoDb.Comparison.Models;

namespace Runtime.Engine.MongoDb.Tests.Comparison;

public class TenantComparisonReportTests
{
    [Fact]
    public void CanCreateSchema()
    {
        JsonSerializerOptions options = JsonSerializerOptions.Default;
        JsonNode schema = options.GetJsonSchemaAsNode(typeof(TenantComparisonReport));
        
        string schemaText = schema.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        Assert.NotEmpty(schemaText);
    }
}
