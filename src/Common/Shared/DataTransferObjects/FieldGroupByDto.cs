using System.Collections.Generic;

// ReSharper disable PropertyCanBeMadeInitOnly.Global

namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

public class FieldGroupByDto
{
    public IEnumerable<string> AttributeNames { get; set; } = null!;
    public IEnumerable<string>? CountAttributeNames { get; set; }
    public IEnumerable<string>? MaxValueAttributeNames { get; set; }
    public IEnumerable<string>? MinValueAttributeNames { get; set; }
    public IEnumerable<string>? AvgAttributeNames { get; set; }
}
