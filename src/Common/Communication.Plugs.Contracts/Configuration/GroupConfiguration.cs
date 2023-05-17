using System;
using System.Collections.Generic;

namespace Meshmakers.Octo.Communication.Plugs.Contracts.Configuration;

public class GroupConfiguration
{
    public IReadOnlyCollection<MappingConfiguration> Mappings { get; set; } = null!;
    public string Name { get; set; } = null!;
    public Guid Id { get; set; } = Guid.Empty;
}