using System;

namespace Meshmakers.Octo.Common.Shared;

public readonly struct RtEntityId : IComparable<RtEntityId>, IEquatable<RtEntityId>
{
    public string CkId { get; }
    public OctoObjectId RtId { get; }

    public RtEntityId(string ckId, OctoObjectId rtId)
    {
        CkId = ckId;
        RtId = rtId;
    }

    public int CompareTo(RtEntityId other)
    {
        var num = string.CompareOrdinal(CkId, other.CkId);
        if (num != 0)
        {
            return num;
        }

        return RtId.CompareTo(other.RtId);
    }

    /// <summary>Compares this ObjectId to another object.</summary>
    /// <param name="obj">The other object.</param>
    /// <returns>True if the other object is an ObjectId and equal to this one.</returns>
    public override bool Equals(object? obj)
    {
        return obj is RtEntityId rhs && Equals(rhs);
    }

    public bool Equals(RtEntityId other)
    {
        return string.CompareOrdinal(CkId, other.CkId) == 0 &&
               Equals(RtId, other.RtId);
    }

    /// <summary>Gets the hash code.</summary>
    /// <returns>The hash code.</returns>
    public override int GetHashCode()
    {
        return CkId.GetHashCode() ^ RtId.GetHashCode();
    }


    /// <summary>Compares two RtEntityId.</summary>
    /// <param name="lhs">The first RtEntityId.</param>
    /// <param name="rhs">The other RtEntityId.</param>
    /// <returns>True if the two RtEntityIds are equal.</returns>
    public static bool operator ==(RtEntityId lhs, RtEntityId rhs)
    {
        return lhs.Equals(rhs);
    }

    /// <summary>Compares two RtEntityIds.</summary>
    /// <param name="lhs">The first RtEntityId.</param>
    /// <param name="rhs">The other RtEntityId.</param>
    /// <returns>True if the two RtEntityIds are not equal.</returns>
    public static bool operator !=(RtEntityId lhs, RtEntityId rhs)
    {
        return !(lhs == rhs);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        return $"CkId: '{CkId}', RtId: '{RtId}'";
    }
}
