namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

public class TextSearchFilter
{
    public TextSearchFilter(object searchTerm)
    {
        SearchTerm = searchTerm;
    }

    public object SearchTerm { get; }
}
