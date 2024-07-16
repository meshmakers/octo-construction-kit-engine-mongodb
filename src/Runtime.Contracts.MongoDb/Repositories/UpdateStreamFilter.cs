using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;

public class UpdateStreamFilter
{
    public UpdateTypes UpdateTypes { get; set; }

    public OctoObjectId? RtId { get; set; }
}