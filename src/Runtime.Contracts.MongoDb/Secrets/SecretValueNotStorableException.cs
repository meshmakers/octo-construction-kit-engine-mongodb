using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Runtime.Contracts.MongoDb.Secrets;

/// <summary>
///     Thrown by the MongoDB repository when an <see cref="RtSecretValue" /> that is not
///     <see cref="RtSecretValueState.Protected" /> is about to be written (AB#5528, concept §3.3 /
///     §8 "write paths that skip encryption"). A <see cref="RtSecretValueState.Pending" /> value is a
///     plaintext the engine write step (AB#5532) has not encrypted; a
///     <see cref="RtSecretValueState.LegacyPlaintext" /> value is a stored plaintext that must be
///     protected (or, for the emergency <c>Decrypt</c> sweep, written as a plain string) rather than
///     round-tripped as a secret. Refusing both keeps plaintext out of the database even when a write
///     path forgets the protector.
/// </summary>
/// <remarks>
///     The message never contains the value. AB#5533 defines the type in the MongoDB contracts; it
///     belongs next to the other secret exceptions in the engine's <c>Runtime.Contracts/Secrets</c>
///     and moves there in a follow-up.
/// </remarks>
public sealed class SecretValueNotStorableException : InvalidOperationException
{
    /// <summary>
    ///     Creates a new instance.
    /// </summary>
    /// <param name="state">State of the refused value</param>
    public SecretValueNotStorableException(RtSecretValueState state)
        : base(state == RtSecretValueState.Pending
            ? "A pending (unencrypted) secret value cannot be stored. The engine write step must protect it " +
              "with ISecretAttributeProtector before it reaches the repository (AB#5532)."
            : $"A secret value in state '{state}' cannot be stored as a secret. Protect it with " +
              "ISecretAttributeProtector first; only protected envelopes are written as secrets.")
    {
        State = state;
    }

    /// <summary>
    ///     State of the refused value.
    /// </summary>
    public RtSecretValueState State { get; }
}
