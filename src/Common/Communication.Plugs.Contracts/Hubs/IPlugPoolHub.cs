namespace Meshmakers.Octo.Communication.Plugs.Contracts.Hubs;

public interface IPlugPoolHub
{
    Task RegisterPlugPoolOperatorAsync(string plugPoolName);
}