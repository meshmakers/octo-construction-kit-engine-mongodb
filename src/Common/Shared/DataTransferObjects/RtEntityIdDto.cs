using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

/// <summary>
/// Represents a unique identifier of a runtime model entity and its construction kit type.
/// </summary>
public class RtEntityIdDto
{
    /// <summary>
    /// Returns the runtime id.
    /// </summary>
    public OctoObjectId RtId { get; set; }
    
    /// <summary>
    /// The construction kit type id.
    /// </summary>
    public CkId<CkTypeId> CkTypeId { get; set; }
}