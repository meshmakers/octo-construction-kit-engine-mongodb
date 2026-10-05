namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Secrets;

/// <summary>
///     Thrown when a query touches a <c>Secret</c> attribute (AB#5528, concept §4.4) with anything
///     other than an <c>IS_NULL</c> / <c>IS_NOT_NULL</c> field filter: comparison operators, sort,
///     attribute (text) search, aggregations and group-by are refused, because they would either
///     compare ciphertext (meaningless) or reveal something about the plaintext.
/// </summary>
/// <remarks>
///     AB#5533 defines the type in the MongoDB contracts for now; it belongs next to the other secret
///     exceptions in <c>Runtime.Contracts/Secrets</c> of the engine and moves there in a follow-up.
///     The message never contains a value.
/// </remarks>
[Serializable]
public sealed class SecretAttributeNotQueryableException : OperationFailedException
{
    /// <summary>
    ///     Creates a new instance.
    /// </summary>
    /// <param name="attributePath">The attribute path of the Secret attribute</param>
    /// <param name="operation">What was attempted, e.g. <c>filter operator 'Equals'</c> or <c>sort</c></param>
    /// <param name="entityName">The CK type or record the path was resolved against</param>
    public SecretAttributeNotQueryableException(string attributePath, string operation, string entityName)
        : base($"Attribute '{attributePath}' of '{entityName}' is a Secret attribute and cannot be used for " +
               $"{operation}. Secret attributes only support the field filter operators IS_NULL and IS_NOT_NULL.")
    {
        AttributePath = attributePath;
        Operation = operation;
        EntityName = entityName;
    }

    /// <summary>
    ///     The attribute path of the Secret attribute.
    /// </summary>
    public string AttributePath { get; }

    /// <summary>
    ///     What was attempted with the attribute.
    /// </summary>
    public string Operation { get; }

    /// <summary>
    ///     The CK type or record the path was resolved against.
    /// </summary>
    public string EntityName { get; }
}
