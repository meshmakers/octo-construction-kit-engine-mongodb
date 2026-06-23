using Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

namespace Meshmakers.Octo.Runtime.Engine.UnitTests;

// Behavior pinned for CkYamlIndexSnippetWriter — the Stage 2D / AB#4222 YAML emission used
// inside the slow-query index suggester. The snippet is what an operator pastes into a CK
// type source file; format mismatches break that paste, so the shape is tested explicitly.
public class CkYamlIndexSnippetWriterTests
{
    [Fact]
    public void Write_EmptyPaths_ReturnsNull()
    {
        Assert.Null(CkYamlIndexSnippetWriter.Write("Demo/Asset", Array.Empty<string>()));
    }

    [Fact]
    public void Write_SingleAttribute_ProducesExpectedShape()
    {
        // Verified against the on-disk YAML used by real CK types
        // (octo-construction-kit/.../meteringPoint.yaml) — indexType: Ascending,
        // fields > attributePaths > value list.
        var yaml = CkYamlIndexSnippetWriter.Write("Demo/Asset", new[] { "Name" });

        Assert.NotNull(yaml);
        Assert.Contains("# Suggested by OctoMesh Performance Advisor (AB#4222) for Demo/Asset.", yaml);
        Assert.Contains("indexes:", yaml);
        Assert.Contains("  - indexType: Ascending", yaml);
        Assert.Contains("    fields:", yaml);
        Assert.Contains("      - attributePaths:", yaml);
        Assert.Contains("          - Name", yaml);
    }

    [Fact]
    public void Write_CompoundIndex_PreservesOrder()
    {
        // Order matters for compound indexes — the suggester emits ESR-sorted paths and
        // the writer must keep them in that order. We assert by checking the relative
        // positions of the path lines in the output.
        var yaml = CkYamlIndexSnippetWriter.Write("Demo/Asset",
            new[] { "TenantId", "Name", "Status" });

        Assert.NotNull(yaml);
        var tenantIdx = yaml.IndexOf("- TenantId", StringComparison.Ordinal);
        var nameIdx = yaml.IndexOf("- Name", StringComparison.Ordinal);
        var statusIdx = yaml.IndexOf("- Status", StringComparison.Ordinal);

        Assert.True(tenantIdx >= 0 && nameIdx > tenantIdx && statusIdx > nameIdx,
            $"Expected TenantId before Name before Status; got positions {tenantIdx}/{nameIdx}/{statusIdx}");
    }

    [Fact]
    public void Write_DottedCkAttributePath_PreservedVerbatim()
    {
        // Stage 2D's reverse function returns dotted PascalCase for nested record paths
        // (e.g. TimeRange.From). The writer doesn't split or escape those — they go on
        // a single line under attributePaths.
        var yaml = CkYamlIndexSnippetWriter.Write("System.StreamData/RtCkArchive",
            new[] { "TimeRange.From" });

        Assert.NotNull(yaml);
        Assert.Contains("- TimeRange.From", yaml);
    }
}
