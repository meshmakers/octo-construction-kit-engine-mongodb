using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Services.RestoreSystemModelCheck;

/// <summary>
///     The lowest version of a service-managed model the platform accepts in a tenant, because the security restrictions
///     of the platform depend on it. A tenant restore never ends with a model below its floor: a genuine older model is
///     raised to the platform's version, and a restore that cannot reach the floor is refused.
/// </summary>
internal static class ServiceManagedCkModelFloors
{
    private static readonly IReadOnlyDictionary<string, CkVersion> Floors =
        new Dictionary<string, CkVersion>(StringComparer.OrdinalIgnoreCase)
        {
            // The first System.Identity whose types carry the writeAccess restriction (engine message 144): below it,
            // generic writes to identity types are open.
            ["System.Identity"] = new(3, 6, 0)
        };

    /// <summary>The floor of <paramref name="modelName" />, or null when the platform sets none.</summary>
    internal static CkVersion? FloorOf(string modelName) =>
        Floors.TryGetValue(modelName.Trim(), out var floor) ? floor : default(CkVersion?);
}
