namespace Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable ClassNeverInstantiated.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global
// ReSharper disable UnusedMember.Global

/// <summary>
/// Represents the configuration of a pool for data transfer.
/// </summary>
public record PoolConfigurationDto
{
    /// <summary>
    /// Gets or sets communication adapters associated with the pool.
    /// </summary>
    public IEnumerable<PoolCommunicationAdapterDto> CommunicationAdapterList { get; set; } = null!;
}