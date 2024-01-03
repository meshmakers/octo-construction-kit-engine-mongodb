namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository.Entities;

public class AutoCompleteText
{
    public int OccurrenceCount { get; set; }

    public string Text { get; set; } = null!;
}