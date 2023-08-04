using System;
using Meshmakers.Octo.Common.Shared;

namespace Persistence.Contracts;

public readonly struct CkId<TKey> : IComparable<CkId<TKey>>, IEquatable<CkId<TKey>> where TKey : struct, IComparable<TKey>, ICkKey
{
    public bool Equals(CkId<TKey> other)
    {
        return ModelId.Equals(other.ModelId) && Key.Equals(other.Key);
    }

    public int CompareTo(CkId<TKey> other)
    {
        var result = ModelId.CompareTo(other.ModelId);
        if (result != 0)
        {
            return result;
        }

        return Key.CompareTo(other.Key);
    }

    public override bool Equals(object? obj)
    {
        return obj is CkId<TKey> other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(ModelId, Key);
    }

    public CkId(CkModelId modelId, TKey key)
    {
        ModelId = modelId;
        Key = key;
    }

    public CkId(string ckId)
    {
        var modelIndex =ckId.IndexOf("/", StringComparison.Ordinal);
        if (modelIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ckId), ckId, $"{nameof(ckId)} must contain a model id and a type id");
        }
        ModelId = ckId.Substring(0, modelIndex);
       
        var typeId = ckId.Substring(modelIndex + 1);
        Key = (TKey) Activator.CreateInstance(typeof(TKey), new[] { typeId });
    }

    public CkModelId ModelId { get;  }
    
    public TKey Key { get;}
    
    public string FullName => $"{ModelId.FullName}/{Key}";
    
    public string SemanticVersionedFullName => $"{ModelId.SemanticVersionedFullName}/{Key.SemanticVersionedFullName}";
    
    public static bool operator ==(CkId<TKey> p1, CkId<TKey> p2)
    {
        return p1.Equals(p2);
    }

    public static bool operator !=(CkId<TKey> p1, CkId<TKey> p2)
    {
        return !p1.Equals(p2);
    }
    
    public static implicit operator CkId<TKey>(string value)
    {
        return new CkId<TKey>(value);
    }

    public override string ToString()
    {
        return FullName;
    }
}