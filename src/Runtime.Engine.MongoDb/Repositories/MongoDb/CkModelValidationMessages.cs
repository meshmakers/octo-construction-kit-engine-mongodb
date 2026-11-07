using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Messages;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.MongoDb;

/// <summary>
///     The import pre-checks of <see cref="DatabaseCkModelRepository" /> as operation messages (AB#8492). The import throws
///     a <see cref="ModelValidationException" /> with <see cref="OperationMessage.MessageText" />, so its text is unchanged;
///     the read-only validation reports the same message with number and parameters.
/// </summary>
internal static class CkModelValidationMessages
{
    /// <summary>
    ///     Number of the tenant validation refusals of AB#8492 that have no number in the engine message catalog. The
    ///     parameter <c>reason</c> tells them apart: <see cref="ReasonSystemReferenceMismatch" />,
    ///     <see cref="ReasonDependencyResolution" />.
    /// </summary>
    public const int TenantValidationNumber = 8492;

    /// <summary>The model was compiled against another system model version than the installed one.</summary>
    public const string ReasonSystemReferenceMismatch = "system-reference-mismatch";

    /// <summary>The resolver refused the model against the installed models (e.g. a dependency the tenant does not have).</summary>
    public const string ReasonDependencyResolution = "dependency-resolution";

    /// <summary>Number of CkLanguageNotSupported in the construction kit engine message catalog.</summary>
    public const int CkLanguageNotSupportedNumber = 91;

    /// <summary>Number of CkModelRequiresNewerEngine in the construction kit engine message catalog.</summary>
    public const int RequiresNewerEngineNumber = 126;

    public static OperationMessage CkLanguageNotSupported(CkModelId modelId, int? ckLanguage, int maxCkLanguage) =>
        new(MessageLevel.Error, null, CkLanguageNotSupportedNumber,
            $"CK model '{modelId}' declares ckLanguage {ckLanguage}, but this engine " +
            $"supports up to {maxCkLanguage} (CkLanguageNotSupported). " +
            "Update the services to an engine version that supports it.",
            new Dictionary<string, string>
            {
                ["modelId"] = modelId.ToString(),
                ["ckLanguage"] = ckLanguage?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                ["maxCkLanguage"] = maxCkLanguage.ToString(System.Globalization.CultureInfo.InvariantCulture)
            });

    public static OperationMessage RequiresNewerEngine(CkModelId modelId, string? minEngineVersion,
        string engineVersion) =>
        new(MessageLevel.Error, null, RequiresNewerEngineNumber,
            $"CK model '{modelId}' requires construction kit engine version " +
            $"{minEngineVersion} or later (minEngineVersion, CkModelRequiresNewerEngine); this engine is " +
            $"version {engineVersion}. Update the service before importing the model.",
            new Dictionary<string, string>
            {
                ["modelId"] = modelId.ToString(),
                ["minEngineVersion"] = minEngineVersion ?? string.Empty,
                ["engineVersion"] = engineVersion
            });

    public static OperationMessage SystemReferenceMismatch(CkModelId modelId, CkModelId referencedModelId,
        string installedModelName, object installedVersion) =>
        new(MessageLevel.Error, null, TenantValidationNumber,
            $"Model '{modelId}' was compiled against '{referencedModelId.FullName}' " +
            $"but '{installedModelName}-{installedVersion}' is installed. " +
            $"The model needs to be recompiled against the current system version.",
            new Dictionary<string, string>
            {
                ["reason"] = ReasonSystemReferenceMismatch,
                ["modelId"] = modelId.ToString(),
                ["referencedModelId"] = referencedModelId.FullName,
                ["installedModelId"] = $"{installedModelName}-{installedVersion}"
            });

    public static OperationMessage DependencyResolutionRefused(CkModelId modelId, string reasonText) =>
        new(MessageLevel.Error, null, TenantValidationNumber, reasonText,
            new Dictionary<string, string>
            {
                ["reason"] = ReasonDependencyResolution,
                ["modelId"] = modelId.ToString()
            });
}
