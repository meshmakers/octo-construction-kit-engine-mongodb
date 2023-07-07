namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

public class MutationDto<TItemType> : MutationDto
{
    public TItemType? Item { get; set; }
}

public class MutationDto
{
    public OctoObjectId RtId { get; set; }
}
