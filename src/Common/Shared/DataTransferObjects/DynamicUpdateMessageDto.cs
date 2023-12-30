namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

public class DynamicUpdateMessageDto<TItem> where TItem : GraphQlDto
{
    // ReSharper disable once UnusedAutoPropertyAccessor.Global
    public ICollection<TItem>? Items { get; set; }
}