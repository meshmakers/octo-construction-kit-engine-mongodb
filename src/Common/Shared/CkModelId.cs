using System;
using System.Diagnostics;

namespace Meshmakers.Octo.Common.Shared;

[DebuggerDisplay("{" + nameof(ModelId) + "} ({" + nameof(ModelVersion) + "})")]
public class CkModelId
{
    public CkModelId(string ckModelId)
    {
        var versionIndex = ckModelId.IndexOf("-", StringComparison.Ordinal);
        if (versionIndex > 0)
        {
            ModelId = ckModelId.Substring(0, versionIndex);
            ModelVersion = ckModelId.Substring(versionIndex + 1);
        }
        else
        {
            ModelId = ckModelId;
            ModelVersion = "1.0.0";
        }
    }
    
    public CkModelId(string modelId, string modelVersion)
    {
        ModelId = modelId;
        ModelVersion = modelVersion;
    }

    protected CkModelId()
    {
    }

    public static implicit operator CkModelId(string value)
    {
        return new CkModelId(value);
    }
    
    public string? ModelId { get; protected init; }
    public CkVersion? ModelVersion { get; protected set; }
    
    public virtual string FullName => $"{ModelId}-{ModelVersion}";

}