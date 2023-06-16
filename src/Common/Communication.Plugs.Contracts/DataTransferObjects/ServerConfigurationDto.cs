namespace Meshmakers.Octo.Communication.Plugs.Contracts.DataTransferObjects;
// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable ClassNeverInstantiated.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global

/// <summary>
/// Gets or sets the configuration of a server a plug in connecting to for data transfer.
/// </summary>
public record ServerConfigurationDto
{
    /// <summary>
    /// The name of the server.
    /// </summary>
    public string Server { get; init; } = null!;

    /// <summary>
    /// Gets or sets the group configurations what is ready from the given server.
    /// </summary>
    public IReadOnlyCollection<GroupConfigurationDto> Groups { get; init; } = null!;

    /// <inheritdoc />
    public virtual bool Equals(ServerConfigurationDto? other)
    {
        if (other == null)
            return false;
        var b = Groups.All(x => other.Groups.Any(y=> y.Equals(x)));
        return Server.Equals(other.Server) && b;
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return HashCode.Combine(Server, Groups);
    }
}