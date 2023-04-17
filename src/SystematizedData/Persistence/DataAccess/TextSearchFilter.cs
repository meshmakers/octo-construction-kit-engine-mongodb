namespace Meshmakers.Octo.Backend.Persistence.DataAccess;

public class TextSearchFilter
{
    public TextSearchFilter(object searchTerm)
    {
        SearchTerm = searchTerm;
    }

    public object SearchTerm { get; }
}
