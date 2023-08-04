using Meshmakers.Octo.Common.Shared;
using Persistence.Contracts;

namespace Meshmakers.Octo.SystematizedData.Persistence;

[AttributeUsage(AttributeTargets.Class)]
public class CkIdAttribute : Attribute
{
    public CkIdAttribute(string modelId, string ckId)
    {
        CkId = new CkId<CkTypeId>(modelId, ckId);
    }

    public CkId<CkTypeId> CkId { get; }
}
