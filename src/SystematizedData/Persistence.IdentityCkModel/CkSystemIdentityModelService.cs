using Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;
using Meshmakers.Octo.SystematizedData.Persistence.Commands;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using NLog;
using Persistence.InternalContracts;

namespace Persistence.IdentityCkModel;

public class CkSystemIdentityModelService : ICkSystemIdentityModelService
{
    private readonly IImportCkModelCommand _importCkModelCommand;
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public CkSystemIdentityModelService(IImportCkModelCommand importCkModelCommand)
    {
        _importCkModelCommand = importCkModelCommand;
    }
    
    public async Task ImportAsync(IOctoSession systemSession, ITenantCkModelRepository ckModelRepository)
    {
        var ckModelFilePath = Path.Combine(Helper.AssemblyDirectory, "ck-system-identity.json");
        Logger.Info("Importing construction kit model '{CkModelFilePath}'", ckModelFilePath);
        await _importCkModelCommand.ImportAsync(systemSession, ckModelRepository, ckModelFilePath);
        Logger.Info("Construction kit model imported.");
    }
}