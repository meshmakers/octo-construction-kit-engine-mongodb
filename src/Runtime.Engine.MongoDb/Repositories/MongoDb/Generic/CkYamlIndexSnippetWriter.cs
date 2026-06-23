using System.Text;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb.Generic;

/// <summary>
/// Renders a Stage 2D / AB#4222 CK-YAML snippet shaped like the CK type's <c>indexes:</c>
/// array. Operators paste this directly into the CK type's source YAML so subsequent model
/// re-imports re-create the same MongoDB index via the existing
/// <c>CkTypeIndexDto</c> + <c>MongoDbRepositoryDataSource.PrepareAndCreateIndex</c> machinery.
/// </summary>
/// <remarks>
/// <para>
/// We hand-format the YAML rather than going through <c>YamlDotNet</c>'s serializer for two
/// reasons:
/// </para>
/// <list type="bullet">
///   <item>The output needs a leading comment that explains how to paste it. YamlDotNet would
///         strip that on round-trip; we'd need a separate prefix concat anyway.</item>
///   <item>The snippet is a partial document under the <c>indexes:</c> key — not a complete
///         document. A serializer would either emit a complete document with extra noise or
///         require a fragile partial-serialization configuration.</item>
/// </list>
/// <para>
/// The output mirrors the actual on-disk shape used by real CK types (verified against
/// <c>octo-construction-kit/src/ConstructionKits/Octo.Sdk.Demo/ConstructionKit/types/meteringPoint.yaml</c>):
/// </para>
/// <code>
/// indexes:
///   - indexType: Ascending
///     fields:
///       - attributePaths:
///           - Name
/// </code>
/// </remarks>
internal static class CkYamlIndexSnippetWriter
{
    /// <summary>
    /// Builds the snippet for a single compound index. <paramref name="ckAttributePaths"/>
    /// must be already in ESR order; the caller (the suggester) is responsible for
    /// equality-first / range-last sorting before formatting. Returns null when no paths
    /// are supplied — there's nothing to suggest.
    /// </summary>
    /// <param name="ckTypeFullName">CK type the index belongs to, used only in the leading comment.</param>
    /// <param name="ckAttributePaths">PascalCase CK attribute paths (e.g. <c>Name</c>, <c>TimeRange.From</c>) in compound-key order.</param>
    public static string? Write(string ckTypeFullName, IReadOnlyList<string> ckAttributePaths)
    {
        if (ckAttributePaths.Count == 0)
        {
            return null;
        }

        var sb = new StringBuilder();

        // Leading comment is intentionally part of the snippet — when the operator pastes
        // into their CK type source, the comment travels with the index spec and serves as
        // the audit trail of where the suggestion came from.
        sb.AppendLine($"# Suggested by OctoMesh Performance Advisor (AB#4222) for {ckTypeFullName}.");
        sb.AppendLine("# Paste under the `indexes:` array of the matching CK type's source YAML.");
        sb.AppendLine("# A subsequent model import will (re-)create the corresponding MongoDB index.");
        sb.AppendLine("indexes:");
        sb.AppendLine("  - indexType: Ascending");
        sb.AppendLine("    fields:");
        sb.AppendLine("      - attributePaths:");

        foreach (var path in ckAttributePaths)
        {
            sb.Append("          - ");
            sb.AppendLine(path);
        }

        return sb.ToString();
    }
}
