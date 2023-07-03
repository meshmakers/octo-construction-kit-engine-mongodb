namespace Meshmakers.Octo.SystematizedData.Persistence.DatabaseEntities;

public class AutoCompleteText : IAutoCompleteText
{
   public int OccurrenceCount { get; set; }

   public string Text { get; set; } = null!;
}
