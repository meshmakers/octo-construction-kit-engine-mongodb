using Meshmakers.Octo.Common.Shared;

// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable ClassNeverInstantiated.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global

namespace Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

/// <summary>
/// Represents a communication adapter in a pool for data transfer.
/// </summary>
public record PoolCommunicationAdapterDto
{
    /// <summary>
    /// Gets or sets the object identifier of the pool.
    /// </summary>
    public OctoObjectId PoolRtId { get; set; }
    
    /// <summary>
    /// Gets or sets the name of the pool.
    /// </summary>
    public string PoolName { get; set; } = null!;
    
    /// <summary>
    /// Gets or sets the object identifier of the communication adapter.
    /// </summary>
    public OctoObjectId AdapterRtId { get; set; }
    
    /// <summary>
    /// Gets or sets the construction kit identifier of the communication adapter.
    /// </summary>
    public string AdapterCkTypeId { get; set; } = null!;
    
    /// <summary>
    /// Gets or sets the docker image name of the communication adapter.
    /// </summary>
    public string ImageName { get; set; } = null!;
    
    /// <summary>
    /// Gets or sets the docker image version of the communication adapter.
    /// </summary>
    public string Version { get; set; } = null!;
}