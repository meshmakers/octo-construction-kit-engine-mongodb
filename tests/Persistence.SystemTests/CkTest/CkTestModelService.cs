using Meshmakers.Common.Shared;
using Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;
using Meshmakers.Octo.SystematizedData.Persistence.Commands;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using NLog;
using Persistence.InternalContracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.SystemTests.CkTest;

public class CkTestModelService : ICkSystemModelService
{
    private readonly IImportCkModelCommand _importCkModelCommand;
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public CkTestModelService(IImportCkModelCommand importCkModelCommand)
    {
        _importCkModelCommand = importCkModelCommand;
    }
    
    public async Task ImportAsync(IOctoSession systemSession, ITenantCkModelRepository ckModelRepository)
    {
        var ckModelFilePath = Path.Combine(typeof(CkTestModelService).Assembly.GetAssemblyDirectory(), "ck-test.json");
        Logger.Info("Importing construction kit model '{CkModelFilePath}'", ckModelFilePath);
        await _importCkModelCommand.ImportAsync(systemSession, ckModelRepository, ckModelFilePath);
        Logger.Info("Construction kit model imported.");
    }
}