namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

public class MutationDto<TItemType> : MutationDto
{
    public TItemType? Item { get; init; }
}

public class MutationDto
{
    public OctoObjectId RtId { get; init; }
}
