using System.Security.Claims;

// ReSharper disable UnusedAutoPropertyAccessor.Global

// ReSharper disable MemberCanBePrivate.Global

namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

/// <summary>
///     Data Transfer Object of diagnostics data
/// </summary>
public class DiagnosticsDto
{
    public DiagnosticsDto(ClaimsPrincipal claimsPrincipal)
    {
        Name = claimsPrincipal.Identity?.Name;
        Claims = claimsPrincipal.Claims.Select(x => new KeyValueDto(x));
    }

    /// <summary>
    ///     Returns the identity name
    /// </summary>
    public string? Name { get; }

    /// <summary>
    ///     Returns claims
    /// </summary>
    public IEnumerable<KeyValueDto> Claims { get; }
}