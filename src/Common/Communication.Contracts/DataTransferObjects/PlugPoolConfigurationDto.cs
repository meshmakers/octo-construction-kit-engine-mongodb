namespace Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable ClassNeverInstantiated.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global
// ReSharper disable UnusedMember.Global

/// <summary>
/// Represents the configuration of a plug pool for data transfer.
/// </summary>
public record PlugPoolConfigurationDto
{
    /// <summary>
    /// Gets or sets plugs associated with the pool.
    /// </summary>
    public IEnumerable<PlugPoolPlugDto> Plugs { get; init; } = null!;
}