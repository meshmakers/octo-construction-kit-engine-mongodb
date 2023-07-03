using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

public interface ICkEntityAssociation
{
    /// <summary>
    ///     Returns the mongodb ID
    /// </summary>
    OctoObjectId AssociationId { get; set; }

    ScopeIds ScopeId { get; set; }
    string OriginCkId { get; set; }
    string TargetCkId { get; set; }

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

    string RoleId { get; set; }
}