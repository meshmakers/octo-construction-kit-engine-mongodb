namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;

public class CkTypeIndex
{
    public IndexTypes IndexType { get; set; }

    public string? Language { get; set; }

    public ICollection<CkIndexFields> Fields { get; set; } = null!;

    public bool CompareTo(CkTypeIndex? other)
    {
        if (other != null)
        {
            if (ReferenceEquals(this, other))
            {
                return true;
            }

            return IndexType == other.IndexType &&
                   Language == other.Language &&
                   Fields.All(f => other.Fields.Any(of => of.CompareTo(f)));
        }

        return false;
    }

    public bool CompareToInSequence(CkTypeIndex? other)
    {
        if (other != null)
        {
            if (ReferenceEquals(this, other))
            {
                return true;
            }

            // Check if fields are in the same order
            return IndexType == other.IndexType &&
                   Language == other.Language &&
                   Fields.Count == other.Fields.Count &&
                   Fields.Zip(other.Fields, (f1, f2) => f1.CompareToInSequence(f2)).All(b => b);
        }

        return false;
    }
}
