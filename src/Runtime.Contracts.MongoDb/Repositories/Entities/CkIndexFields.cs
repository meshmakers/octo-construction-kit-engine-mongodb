namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;

public class CkIndexFields
{
    public int? Weight { get; set; }

    public ICollection<string> AttributeNames { get; set; } = null!;

    public bool CompareTo(CkIndexFields? other)
    {
        if (other != null)
        {
            if (ReferenceEquals(this, other))
            {
                return true;
            }

            return Weight == other.Weight &&
                   AttributeNames.Order().SequenceEqual(other.AttributeNames.Order(), StringComparer.OrdinalIgnoreCase);
        }

        return false;
    }
    
    public bool CompareToInSequence(CkIndexFields? other)
    {
        if (other != null)
        {
            if (ReferenceEquals(this, other))
            {
                return true;
            }

            return Weight == other.Weight &&
                   AttributeNames.SequenceEqual(other.AttributeNames, StringComparer.OrdinalIgnoreCase);
        }

        return false;
    }
}
