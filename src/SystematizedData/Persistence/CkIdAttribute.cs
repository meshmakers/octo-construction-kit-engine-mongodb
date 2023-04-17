using System;
using Meshmakers.Common.Shared;

namespace Meshmakers.Octo.SystematizedData.Persistence;

[AttributeUsage(AttributeTargets.Class)]
public class CkIdAttribute : Attribute
{
    public CkIdAttribute(string ckId)
    {
        ArgumentValidation.ValidateString(nameof(ckId), ckId);

        CkId = ckId;
    }

    public string CkId { get; }
}
