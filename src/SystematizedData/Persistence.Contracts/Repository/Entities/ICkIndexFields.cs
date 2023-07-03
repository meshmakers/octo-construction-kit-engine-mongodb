using System.Collections.Generic;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

public interface ICkIndexFields
{
    int? Weight { get; set; }
    ICollection<string> AttributeNames { get; set; }
}