using System.Collections.Generic;
using Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

namespace Meshmakers.Octo.SystematizedData.Persistence.Commands;

internal class TransientCkModel
{
    public TransientCkModel()
    {
        CkEntityAssociations = new List<CkEntityAssociation>();
        CkEntityInheritances = new List<CkEntityInheritance>();
        CkEntities = new List<CkEntity>();
        CkAttributes = new List<CkAttribute>();
    }

    public List<CkEntityAssociation> CkEntityAssociations { get; }
    public List<CkEntityInheritance> CkEntityInheritances { get; }
    public List<CkEntity> CkEntities { get; }
    public List<CkAttribute> CkAttributes { get; }
}
