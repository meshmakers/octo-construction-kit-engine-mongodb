using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.Backend.Persistence.DataAccess;

public class UpdateStreamFilter
{
    public UpdateTypes UpdateTypes { get; set; }

    public OctoObjectId? RtId { get; set; }
}
