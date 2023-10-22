using System;
using System.Collections.Generic;
using System.Linq;

namespace Meshmakers.Octo.SystematizedData.Persistence.DataAccess;

[Serializable]
public class DuplicateKeyException : Exception
{
    public DuplicateKeyException(string message, Type type, Exception inner) : base(message, inner)
    {
        Type = type;
        Properties = Enumerable.Empty<string>();
    }

    /// <summary>
    ///     Constructor for indicating a unique constraint violation.
    ///     Contain information which properties of an object
    ///     are responsible for the violation.
    /// </summary>
    public DuplicateKeyException(
        string message,
        Type type,
        IEnumerable<string> properties,
        Exception innerException)
        : base(message, innerException)
    {
        Type = type;
        Properties = properties;
    }

    /// <summary>
    ///     The entity type for which the unique constraint was violated
    /// </summary>
    public Type Type { get; }

    /// <summary>
    ///     The properties involved in the unique constraint violation
    /// </summary>
    public IEnumerable<string> Properties { get; }
}
