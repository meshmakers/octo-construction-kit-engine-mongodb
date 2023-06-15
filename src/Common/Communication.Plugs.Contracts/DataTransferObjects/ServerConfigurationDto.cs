namespace Meshmakers.Octo.Communication.Plugs.Contracts.DataTransferObjects;

public record ServerConfigurationDto
{
    public string Server { get; set; } = null!;

    public IReadOnlyCollection<GroupConfigurationDto> Groups { get; set; } = null!;

    public virtual bool Equals(ServerConfigurationDto? other)
    {
        if (other == null)
            return false;
        var b = Groups.All(x => other.Groups.Any(y=> y.Equals(x)));
        return Server.Equals(other.Server) && b;
    }
}