using System;
using System.Diagnostics;

namespace Meshmakers.Octo.Common.Shared;

/// <summary>
/// Represents a versioned construction kit association id
/// </summary>
[DebuggerDisplay("{" + nameof(AssociationId) + "} ({" + nameof(Version) + "})")]
[System.Text.Json.Serialization.JsonConverter(typeof(CkAssociationIdConverter))]
public readonly struct CkAssociationId : IComparable<CkAssociationId>, IEquatable<CkAssociationId>, ICkKey
{
    /// <summary>
    /// Defines the name of the association, e. g. "ParentChild"
    /// </summary>
    public string AssociationId { get; }
    
    public CkVersion Version { get; }

    public string FullName => IsEmpty ? "" : $"{AssociationId}-{Version}";

    public string SemanticVersionedFullName
    {
        get
        {
            if (IsEmpty)
            {
                return "";
            }
            
            var s = AssociationId;
            if (Version.Major > 1)
            {
                s += $"-{Version.Major}";
            }

            return s;
        }
    }

    public bool IsEmpty => string.IsNullOrWhiteSpace(AssociationId);

    public CkAssociationId(string associationId)
    {
        var typeIndex = associationId.IndexOf("-", StringComparison.Ordinal);
        if (typeIndex < 0)
        {
            AssociationId = associationId;
            Version = "1.0.0";
        }
        else
        {
            AssociationId = associationId.Substring(0, typeIndex);
            Version = associationId.Substring(typeIndex + 1);
        }
        if (string.IsNullOrWhiteSpace(AssociationId))
        {
            throw new ArgumentOutOfRangeException(nameof(associationId), associationId, $"{nameof(associationId)} must contain a type id");
        }
    }

    public CkAssociationId(string associationId, string typeVersion = "1.0.0") 
    {
        AssociationId = associationId;
        Version = typeVersion;
    }
    
    public static implicit operator CkAssociationId(string value)
    {
        return new CkAssociationId(value);
    }

    public int CompareTo(CkAssociationId other)
    {
        var result = String.Compare(AssociationId, other.AssociationId, StringComparison.Ordinal);
        if (result != 0)
        {
            return result;
        }

        return Version.CompareTo(other.Version);
    }

    public bool Equals(CkAssociationId other)
    {
        return AssociationId == other.AssociationId && Equals(Version, other.Version);
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
        return FullName;
    }

    public object ToType(Type conversionType, IFormatProvider? provider)
    {
        switch (Type.GetTypeCode(conversionType))
        {
            case TypeCode.String:
                return ToString(provider);
            case TypeCode.Object:
                if (conversionType == typeof(object))
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
        return FullName;
    }

    public override bool Equals(object? obj)
    {
        if (obj == null)
        {
            return false;
        }
        
        var other = (CkAssociationId)obj;
        
        return AssociationId == other.AssociationId && Version == other.Version;
    }

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = 15;
            hash = hash * 22 + AssociationId.GetHashCode();
            hash = hash * 22 + Version.GetHashCode();
            return hash;
        }
    }
    
    public static bool operator ==(CkAssociationId p1, CkAssociationId p2)
    {
        return p1.Equals(p2);
    }

    public static bool operator !=(CkAssociationId p1, CkAssociationId p2)
    {
        return !p1.Equals(p2);
    }
}