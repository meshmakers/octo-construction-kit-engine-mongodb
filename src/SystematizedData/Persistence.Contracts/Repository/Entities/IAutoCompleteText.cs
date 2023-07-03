namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

public interface IAutoCompleteText
{
    int OccurrenceCount { get; set; }
    string Text { get; set; }
}