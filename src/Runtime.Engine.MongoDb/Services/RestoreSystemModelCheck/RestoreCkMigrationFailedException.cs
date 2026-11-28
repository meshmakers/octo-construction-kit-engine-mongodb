using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Services.RestoreSystemModelCheck;

/// <summary>
///     A CK migration of the raise of a tenant restore reported a failure (AB#10017). An ordinary import only logs a
///     failed migration; inside a <see cref="RestoringTenantScope" /> the raise fails instead, so the restore is refused
///     (<c>upgrade_failed</c>) rather than attaching a raised model whose data was not migrated. The message names the
///     model and the number of errors only, never their text (it may quote tenant data).
/// </summary>
internal sealed class RestoreCkMigrationFailedException(CkModelId modelId, int errorCount)
    : Exception($"The CK migration of '{modelId}' failed with {errorCount} error(s) while a tenant restore raised it.")
{
    /// <summary>The model whose migration failed.</summary>
    public CkModelId ModelId { get; } = modelId;
}
