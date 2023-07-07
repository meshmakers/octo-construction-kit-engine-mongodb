using System;

namespace Meshmakers.Octo.Common.Shared;

public class CkTypeId : IComparable<CkTypeId>, IEquatable<CkTypeId>, IConvertible
{
    public string? ModelId { get; }

    /// <summary>
    /// Defines the name of the entity, e. g. "Person"
    /// </summary>
    public string TypeId { get; }
    
    public CkVersion Version { get; }

    public string FullName => $"{ModelId}/{TypeId}-{Version}";

    public CkTypeId(string ckId)
    {
        var modelIndex =ckId.IndexOf("/", StringComparison.Ordinal);
        if (modelIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ckId), ckId, "CkId must contain a model id and a type id");
        }
        ModelId = ckId.Substring(0, modelIndex);
        if (string.IsNullOrWhiteSpace(ModelId))
        {
            throw new ArgumentOutOfRangeException(nameof(ckId), ckId, "CkId must contain a model id and a type id");
        }
        var typeIndex = ckId.IndexOf("-", StringComparison.Ordinal);
        if (typeIndex < 0)
        {
            TypeId = ckId.Substring(modelIndex + 1);
            Version = "1.0.0";
        }
        else
        {
            TypeId = ckId.Substring(modelIndex + 1, typeIndex - modelIndex - 1);
            Version = ckId.Substring(typeIndex + 1);
        }
        if (string.IsNullOrWhiteSpace(TypeId))
        {
            throw new ArgumentOutOfRangeException(nameof(ckId), ckId, "CkId must contain a type id");
        }
    }

    public CkTypeId(string modelId, string typeId, string typeVersion = "1.0.0") 
    {
        ModelId = modelId;
        TypeId = typeId;
        Version = typeVersion;
    }
    
    public static implicit operator CkTypeId(string value)
    {
        return new CkTypeId(value);
    }

    public int CompareTo(CkTypeId? other)
    {
        if (other == null)
        {
            return 1;
        }

        var result = String.Compare(ModelId, other.ModelId, StringComparison.Ordinal);
        if (result != 0)
        {
            return result;
        }

        result = String.Compare(TypeId, other.TypeId, StringComparison.Ordinal);
        if (result != 0)
        {
            return result;
        }

        return Version.CompareTo(other.Version);
    }

    public bool Equals(CkTypeId? other)
    {
        if (other == null)
        {
            return false;
        }
        
        return ModelId == other.ModelId && TypeId == other.TypeId && Equals(Version, other.Version);
    }

    public TypeCode GetTypeCode()
    {
        return TypeCode.Object;
    }

    public bool ToBoolean(IFormatProvider? provider)
    {
        throw new InvalidCastException();
    }

    public byte ToByte(IFormatProvider? provider)
    {
        throw new InvalidCastException();
    }

    public char ToChar(IFormatProvider? provider)
    {
        throw new InvalidCastException();
    }

    public DateTime ToDateTime(IFormatProvider? provider)
    {
        throw new InvalidCastException();
    }

    public decimal ToDecimal(IFormatProvider? provider)
    {
        throw new InvalidCastException();
    }

    public double ToDouble(IFormatProvider? provider)
    {
        throw new InvalidCastException();
    }

    public short ToInt16(IFormatProvider? provider)
    {
        throw new InvalidCastException();
    }

    public int ToInt32(IFormatProvider? provider)
    {
        throw new InvalidCastException();
    }

    public long ToInt64(IFormatProvider? provider)
    {
        throw new InvalidCastException();
    }

    public sbyte ToSByte(IFormatProvider? provider)
    {
        throw new InvalidCastException();
    }

    public float ToSingle(IFormatProvider? provider)
    {
        throw new InvalidCastException();
    }

    public string ToString(IFormatProvider? provider)
    {
        return $"{ModelId}/{TypeId}-{Version}";
    }

    public object ToType(Type conversionType, IFormatProvider? provider)
    {
        switch (Type.GetTypeCode(conversionType))
        {
            case TypeCode.String:
                return ((IConvertible)this).ToString(provider);
            case TypeCode.Object:
                if (conversionType == typeof(object) || conversionType == typeof(OctoObjectId))
                {
                    return this;
                }

                break;
        }

        throw new InvalidCastException();
    }

    public ushort ToUInt16(IFormatProvider? provider)
    {
        throw new InvalidCastException();
    }

    public uint ToUInt32(IFormatProvider? provider)
    {
        throw new InvalidCastException();
    }

    public ulong ToUInt64(IFormatProvider? provider)
    {
        throw new InvalidCastException();
    }
    
    /// <summary>
    ///     Returns a string representation of the value.
    /// </summary>
    /// <returns>A string representation of the value.</returns>
    public override string ToString()
    {
        return $"{ModelId}.{TypeId}-{Version}";
    }

    public override bool Equals(object? obj)
    {
        if (obj == null)
        {
            return false;
        }
        
        var other = (CkTypeId)obj;
        
        return ModelId == other.ModelId && TypeId == other.TypeId && Version == other.Version;
    }

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 23 + (ModelId?.GetHashCode() ?? 0);
            hash = hash * 23 + (TypeId?.GetHashCode() ?? 0);
            hash = hash * 23 + (Version?.GetHashCode() ?? 0);
            return hash;
        }
    }
}