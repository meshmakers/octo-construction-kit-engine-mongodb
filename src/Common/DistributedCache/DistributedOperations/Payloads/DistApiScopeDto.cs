namespace Meshmakers.Octo.Common.DistributedCache.DistributedOperations.Payloads;

/// <summary>
/// Represents an API scope.
/// </summary>
/// <param name="Name">Name of the scope.</param>
/// <param name="DisplayName">Display name of the scope.</param>
public record DistApiScopeDto(string Name, string DisplayName)
{
    
}