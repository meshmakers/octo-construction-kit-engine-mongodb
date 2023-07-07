using System.Diagnostics;
using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

[DebuggerDisplay("{" + nameof(RoleId) + "} -> {" + nameof(TargetCkId) + "}")]
public class CkEntityAssociation : ICkEntityAssociation
{
    /// <summary>
    ///     Returns the mongodb ID
    /// </summary>
    public OctoObjectId AssociationId { get; set; }

    public CkTypeId OriginCkId { get; set; }= null!;

    public CkTypeId TargetCkId { get; set; }= null!;

    /// <summary>
    ///     Name of the association for inbound references (e. g. Parent)
    /// </summary>
    public string InboundName { get; set; }= null!;

    /// <summary>
    ///     Name of the association for outbound references (e. g. Children)
    /// </summary>
    public string OutboundName { get; set; }= null!;

    /// <summary>
    ///     Multiplicity of the inbound association
    /// </summary>
    public Multiplicities InboundMultiplicity { get; set; }

    /// <summary>
    ///     Multiplicity of the outbound association
    /// </summary>
    public Multiplicities OutboundMultiplicity { get; set; }

    public string RoleId { get; set; } = null!;
}
