using System.Diagnostics;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository.Entities;

[DebuggerDisplay("{" + nameof(RoleId) + "}: {" + nameof(InboundName) + "}->{" + nameof(OutboundName) + "}")]
public class CkAssociationRole
{
    /// <summary>
    ///     Constructor
    /// </summary>
    public CkAssociationRole()
    {
        Attributes = new HashSet<CkTypeAttribute>();
    }

    /// <summary>
    ///     Gets or sets the construction kit model id
    /// </summary>
    public CkModelId CkModelId { get; set; }

    /// <summary>
    ///     Returns the mongodb ID
    /// </summary>
    public CkId<CkAssociationRoleId> RoleId { get; set; }

    /// <summary>
    ///     Name of the association for inbound references (e. g. Parent)
    /// </summary>
    public string InboundName { get; set; } = null!;

    /// <summary>
    ///     Name of the association for outbound references (e. g. Children)
    /// </summary>
    public string OutboundName { get; set; } = null!;

    /// <summary>
    ///     Multiplicity of the inbound association
    /// </summary>
    public MultiplicitiesDto InboundMultiplicity { get; set; }

    /// <summary>
    ///     Multiplicity of the outbound association
    /// </summary>
    public MultiplicitiesDto OutboundMultiplicity { get; set; }

    /// <summary>
    ///     Gets or sets a list of attributes
    /// </summary>
    public ICollection<CkTypeAttribute> Attributes { get; set; }
}