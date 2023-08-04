using Meshmakers.Octo.Common.Shared;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

public interface ICkAssociationRole
{
    /// <summary>
    ///     Returns the mongodb ID
    /// </summary>
    CkId<CkAssociationId> RoleId { get; set; }

    /// <summary>
    ///     Name of the association for inbound references (e. g. Parent)
    /// </summary>
    string InboundName { get; set; }

    /// <summary>
    ///     Name of the association for outbound references (e. g. Children)
    /// </summary>
    string OutboundName { get; set; }

    /// <summary>
    ///     Multiplicity of the inbound association
    /// </summary>
    Multiplicities InboundMultiplicity { get; set; }

    /// <summary>
    ///     Multiplicity of the outbound association
    /// </summary>
    Multiplicities OutboundMultiplicity { get; set; }
}