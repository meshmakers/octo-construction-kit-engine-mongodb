using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository;

public class UpdateAssociationStreamFilter
{
    public UpdateTypes UpdateTypes { get; set; }

    public string RoleId { get; set; } = string.Empty;

    public OctoObjectId? OriginRtId { get; set; }
    public OctoObjectId? TargetRtId { get; set; }
}