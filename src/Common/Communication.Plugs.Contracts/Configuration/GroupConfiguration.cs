using System;
using System.Collections.Generic;
using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.Communication.Plugs.Contracts.Configuration;

public record GroupConfiguration
{
    public IReadOnlyCollection<MappingConfiguration> Mappings { get; set; } = null!;
    public string Name { get; set; } = null!;
    public OctoObjectId Id { get; set; }
}