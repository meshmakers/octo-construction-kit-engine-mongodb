using System.Collections.Generic;

namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

public class RtEntityAttributeDto : GraphQlDto
{
    public string? AttributeName { get; set; }

    public object? Value { get; set; }
    public ICollection<object>? Values { get; set; }
}
