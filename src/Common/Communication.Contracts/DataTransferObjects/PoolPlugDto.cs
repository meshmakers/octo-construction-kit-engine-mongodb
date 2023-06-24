using Meshmakers.Octo.Common.Shared;

// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable ClassNeverInstantiated.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global

namespace Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

/// <summary>
/// Represents a plug in a plug pool for data transfer.
/// </summary>
public record PoolPlugDto
{
    /// <summary>
    /// Gets or sets the object identifier of the plug pool.
    /// </summary>
    public OctoObjectId PlugPoolRtId { get; set; }
    
    /// <summary>
    /// Gets or sets the name of the plug pool.
    /// </summary>
    public string PlugPoolName { get; set; } = null!;
    
    /// <summary>
    /// Gets or sets the object identifier of the plug.
    /// </summary>
    public OctoObjectId PlugRtId { get; set; }
    
    /// <summary>
    /// Gets or sets the docker image name of the plug.
    /// </summary>
    public string ImageName { get; set; } = null!;
    
    /// <summary>
    /// Gets or sets the docker image version of the plug.
    /// </summary>
    public string Version { get; set; } = null!;
}