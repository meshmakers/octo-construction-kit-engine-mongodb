using System.Collections.Generic;

namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

/// <summary>
///     Information about all available identity providers.
/// </summary>
public class IdentityProvidersResult
{
    /// <summary>
    ///     The available identity providers.
    /// </summary>
    public IEnumerable<IdentityProviderDto>? IdentityProviders { get; init; }
}
