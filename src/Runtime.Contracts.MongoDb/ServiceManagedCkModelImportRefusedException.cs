namespace Meshmakers.Octo.Runtime.Contracts.MongoDb;

/// <summary>
///     Thrown by <see cref="ITenantContext.ImportCkModelAsync(Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects.CkCompiledModelRoot)" />
///     when a caller-supplied compiled model is service-managed (<see cref="ServiceManagedCkModels.IsServiceManaged" />).
///     The refusal happens before anything is written: no import lock, no <c>Importing</c> row, no element rows, no
///     migration, no notification, no re-validation. It holds for every version (newer, equal, older, another major).
/// </summary>
/// <remarks>
///     <see cref="Exception.Message" /> starts with <see cref="Code" />, names the model id and the rule and carries no
///     model content, so a job error or a log line can show it as it is.
/// </remarks>
public sealed class ServiceManagedCkModelImportRefusedException : InvalidOperationException
{
    private ServiceManagedCkModelImportRefusedException(string modelId, string message) : base(message)
    {
        ModelId = modelId;
    }

    /// <summary>The stable error code, <see cref="ServiceManagedCkModels.RefusalCode" />.</summary>
    public string Code => ServiceManagedCkModels.RefusalCode;

    /// <summary>The full id (name and version) of the refused model, as the caller supplied it.</summary>
    public string ModelId { get; }

    /// <summary>Builds the refusal for the model id <paramref name="modelId" />.</summary>
    /// <param name="modelId">The full id of the refused model.</param>
    public static ServiceManagedCkModelImportRefusedException Create(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        var message =
            $"{ServiceManagedCkModels.RefusalCode}: '{modelId}' is a service-managed CK model (System, System.*) and " +
            "cannot be imported by a caller. Nothing was imported. Service-managed models are installed by the " +
            "platform services themselves; to re-run their import for a tenant use ClearCache or ReRunTenantSetup.";
        return new ServiceManagedCkModelImportRefusedException(modelId, message);
    }
}
