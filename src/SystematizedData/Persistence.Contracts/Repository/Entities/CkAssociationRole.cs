using System.Diagnostics;
using Meshmakers.Octo.Common.Shared;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

[DebuggerDisplay("{" + nameof(RoleId) + "}: {" + nameof(InboundName) + "}->{" + nameof(OutboundName) + "}")]
public class CkAssociationRole 
{
    /// <summary>
    ///     Returns the mongodb ID
    /// </summary>
    public CkId<CkAssociationRoleId> RoleId { get; set; }

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

}
