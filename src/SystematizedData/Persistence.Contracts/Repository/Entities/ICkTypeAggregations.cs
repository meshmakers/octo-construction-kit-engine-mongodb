using System.Collections.Generic;

namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

public interface ICkTypeAggregations
{
    ICollection<ICkEntityAssociation>? Owned { get; set; }
    ICollection<ICkEntityAssociation>? Inherited { get; set; }
}