using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;

namespace Meshmakers.Octo.Runtime.Engine.MongoDb.Repositories.Entities;

// ReSharper disable once ClassNeverInstantiated.Global
public class CkTypeInfo : CkType
{
    public IEnumerable<CkInheritedTypeInfo> InheritedTypes { get; set; } = new List<CkInheritedTypeInfo>();
    public IEnumerable<CkType> Inheritances { get; set; } = new List<CkType>();
}

// ReSharper disable once ClassNeverInstantiated.Global
