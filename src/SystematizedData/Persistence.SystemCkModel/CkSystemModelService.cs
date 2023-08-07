using Meshmakers.Common.Shared;
using Meshmakers.Octo.SystematizedData.Persistence;
using Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;
using Meshmakers.Octo.SystematizedData.Persistence.Commands;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using NLog;
using Persistence.InternalContracts;

namespace Persistence.SystemCkModel;

public class CkSystemModelService : ICkSystemModelService
{
    private readonly IImportCkModelCommand _importCkModelCommand;
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public CkSystemModelService(IImportCkModelCommand importCkModelCommand)
    {
        _importCkModelCommand = importCkModelCommand;
    }
    
    public async Task ImportAsync(IOctoSession systemSession, ITenantCkModelRepository ckModelRepository)
    {
        var ckModelFilePath = Path.Combine(typeof(CkSystemModelService).Assembly.GetAssemblyDirectory(), "ck-system.json");
        Logger.Info("Importing construction kit model '{CkModelFilePath}'", ckModelFilePath);
        await _importCkModelCommand.ImportAsync(systemSession, ckModelRepository, ckModelFilePath);
        Logger.Info("Construction kit model imported.");
    }
}