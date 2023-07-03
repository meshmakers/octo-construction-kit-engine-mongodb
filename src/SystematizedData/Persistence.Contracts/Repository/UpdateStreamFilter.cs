using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

public class UpdateStreamFilter
{
    public UpdateTypes UpdateTypes { get; set; }

    public OctoObjectId? RtId { get; set; }
}
