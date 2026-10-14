using System.Text;
using System.Text.Json.Nodes;

using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Serialization;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;

using Xunit;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.IntegrationTests.Fixtures;

/// <summary>
///     Range retention is the compiler default (AB#6675), so a model built by the test projects (Test-1.0.0) is
///     range-retaining. Models published before that - and everything a compile with <c>RangeRetention=false</c> still
///     produces - are exact-pinned ("classic"). The classic persistence contract (no ranges, no minEngineVersion,
///     concrete references) is therefore pinned on a classic variant derived from the catalog model.
/// </summary>
public static class ClassicModelFactory
{
    /// <summary>
    ///     The compiled catalog model <paramref name="model" /> as an exact-pinned model: no dependency ranges, no
    ///     minEngineVersion, references into its dependencies concrete (<c>System-2.5.0/...</c> instead of
    ///     <c>System@2/...</c>).
    /// </summary>
    /// <param name="system">The System version the model was resolved against (the embedded System).</param>
    public static async Task<CkCompiledModelRoot> CreateAsync(ICatalogService catalogService,
        ICkJsonSerializer serializer, CkModelId model, CkModelId system)
    {
        var compiled = await catalogService.GetAsync(model, new OperationResult());
        Assert.NotNull(compiled);
        using var stream = new MemoryStream();
        await using (var writer = new StreamWriter(stream, leaveOpen: true))
        {
            await serializer.SerializeAsync(writer, compiled);
            await writer.FlushAsync(TestContext.Current.CancellationToken);
        }

        var node = JsonNode.Parse(Encoding.UTF8.GetString(stream.ToArray()))!.AsObject();
        node.Remove("dependencyRanges");
        node.Remove("minEngineVersion");
        var json = node.ToJsonString().Replace($"{system.Name}@{system.Version.Major}/", $"{system.FullName}/");
        var operationResult = new OperationResult();
        var classic = await serializer.DeserializeCompiledModelRootAsync(json, "classic", operationResult);
        Assert.False(operationResult.HasErrors, string.Join("; ", operationResult.Messages));
        Assert.False(classic.IsRangeRetaining);
        return classic;
    }
}
