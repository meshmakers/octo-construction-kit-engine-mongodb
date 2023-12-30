namespace Meshmakers.Octo.Common.Shared.GraphQL;

public class Edge<TDto>
{
    public string? Cursor { get; set; }

    public TDto? Node { get; set; }
}