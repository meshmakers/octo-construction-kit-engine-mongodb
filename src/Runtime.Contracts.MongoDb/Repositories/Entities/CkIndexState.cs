using System.Diagnostics;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories.Entities;

/// <summary>
/// Defines the state of an index.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
[DebuggerDisplay("Index: {Name}, Collection {CollectionName} State: {State}, Error: {ErrorMessage}")]
public class CkIndexState
{
    /// <summary>
    /// Gets or sets the name of the index.
    /// </summary>
    public string Name { get; set; } = null!;
    
    /// <summary>
    /// Gets or sets the name of the collection the index belongs to.
    /// </summary>
    public string CollectionName { get; set; } = null!;
    
    /// <summary>
    /// Gets or sets the state of the index.
    /// </summary>
    public IndexState State { get; set; }
    
    /// <summary>
    /// Gets or sets the error message if the index failed to be applied.
    /// </summary>
    public string? ErrorMessage { get; set; }
    
    /// <summary>
    /// Gets or sets the date and time when the index was applied.
    /// </summary>
    public DateTime? AppliedAt { get; set; }
}

/// <summary>
/// State of an index (whether it was applied successfully or failed).
/// </summary>
public enum IndexState
{
    /// <summary>
    /// Applied successfully.
    /// </summary>
    Applied,
    
    /// <summary>
    /// Failed to apply.
    /// </summary>
    Failed,
}
