using System;

namespace Meshmakers.Octo.Common.Shared.DataTransferObjects;

[Flags]
public enum UpdateTypesDto
{
    /// <summary>
    ///     Not flags defined.
    /// </summary>
    Undefined = 0,

    /// <summary>An insert operation type.</summary>
    Insert = 1,

    /// <summary>An update operation type.</summary>
    Update = 2,

    /// <summary>A replace operation type.</summary>
    Replace = 4,

    /// <summary>A delete operation type.</summary>
    Delete = 8
}
