namespace Meshmakers.Octo.Runtime.Contracts.MongoDb;

/// <summary>
///     The platform-wide rule which CK models are service-managed: <c>System</c> and every model whose name starts with
///     <c>System.</c>. Such a model enters a tenant only through the embedded import of its owning service or the
///     blueprint/system import path (<see cref="ITenantContext.ImportCkModelAsync(Meshmakers.Octo.ConstructionKit.Contracts.CkModelId, Meshmakers.Octo.ConstructionKit.Contracts.OperationResult)" />);
///     a caller-supplied compiled model of such a name is refused by
///     <see cref="ITenantContext.ImportCkModelAsync(Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects.CkCompiledModelRoot)" />
///     with <see cref="ServiceManagedCkModelImportRefusedException" />.
/// </summary>
/// <remarks>
///     The predicate does not depend on the host: service-managed model descriptors are registered per service (the bot
///     knows only System.Bot), so a guard that asked the host's descriptors would let System.Identity through in the bot.
///     It is the same predicate the asset repository uses for its caller import endpoints.
/// </remarks>
public static class ServiceManagedCkModels
{
    /// <summary>Stable error code of the refusal, shared with the asset repository's REST refusal.</summary>
    public const string RefusalCode = "CK_SERVICE_MANAGED_MODEL";

    /// <summary>
    ///     True for a model name the platform owns: <c>System</c> or a name starting with <c>System.</c>, compared
    ///     case-insensitively after trimming, so that no spelling of the name gets past the refusal. A name that only
    ///     contains "System" elsewhere (<c>Customer.System</c>, <c>SystemX</c>) is not service-managed.
    /// </summary>
    /// <param name="modelName">The model name (without version); null or blank is not service-managed.</param>
    public static bool IsServiceManaged(string? modelName)
    {
        var name = modelName?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        return name.Equals("System", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith("System.", StringComparison.OrdinalIgnoreCase);
    }
}
