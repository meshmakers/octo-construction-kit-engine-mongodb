using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.SystematizedData.Persistence;

[AttributeUsage(AttributeTargets.Class)]
public class CkIdAttribute : Attribute
{
    public CkIdAttribute(string ckId)
    {
        CkId = ckId;
    }

    public CkTypeId CkId { get; }
}
