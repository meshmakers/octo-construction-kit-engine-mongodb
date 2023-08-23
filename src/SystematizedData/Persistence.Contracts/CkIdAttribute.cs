using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence;

[AttributeUsage(AttributeTargets.Class)]
public class CkIdAttribute : Attribute
{
    public CkIdAttribute(string modelId, string key)
    {
        CkId = new CkId<CkTypeId>(modelId, key);
    }
    
    public CkIdAttribute(string ckId)
    {
        CkId = new CkId<CkTypeId>(ckId);
    }

    public CkId<CkTypeId> CkId { get; }
}
