using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

public class UpdateAssociationStreamFilter
{
    public UpdateTypes UpdateTypes { get; set; }
    
    public string RoleId { get; set; } = string.Empty;

    public OctoObjectId? OriginRtId { get; set; }
    public OctoObjectId? TargetRtId { get; set; }
}