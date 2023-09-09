using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.ModelRepositories;
using Meshmakers.Octo.ConstructionKit.Contracts.Serialization;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.SystematizedData.Persistence.CkRuleEngine.Cache;
using Meshmakers.Octo.SystematizedData.Persistence.DataAccess;
using Microsoft.Extensions.Logging;
using Persistence.InternalContracts;

namespace Meshmakers.Octo.SystematizedData.Persistence.Commands;

public class ImportCkModelCommand : IImportCkModelCommand
{
    private readonly ILogger<ImportCkModelCommand> _logger;
    private readonly ICkSerializer _ckSerializer;
    private readonly ICkValidationService _ckValidationService;
    private readonly ICkModelRepositoryManager _ckModelRepositoryManager;

    public ImportCkModelCommand(ILogger<ImportCkModelCommand> logger, ICkSerializer ckSerializer, ICkValidationService ckValidationService,
        ICkModelRepositoryManager ckModelRepositoryManager)
    {
        _logger = logger;
        _ckSerializer = ckSerializer;
        _ckValidationService = ckValidationService;
        _ckModelRepositoryManager = ckModelRepositoryManager;
    }

    public async Task ImportTextAsync(IOctoSession session, ITenantCkModelRepository ckModelRepository, string jsonText,
        CancellationToken? cancellationToken = null)
    {
        try
        {
            _logger.LogInformation("Reading CK model....");
            var operationResult = new OperationResult();
            var model = await _ckSerializer.DeserializeCompiledModelRootAsync(jsonText, operationResult);

            if (model == null)
            {
                _logger.LogInformation("Import of CK model failed, model cannot be deserialized");
                operationResult.WriteMessagesToLogger(_logger);
                throw CommandExecutionFailedException.CannotDeserializeModelFromString(jsonText);
            }

            _logger.LogInformation("Executing import of CK model....");
            await _ckModelRepositoryManager.PublishModelAsync(InternalConstants.CkModelRepositoryName, model, false, ckModelRepository);

            _logger.LogInformation("Import of CK model completed");
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Import of CK model failed");
            throw;
        }
    }

    public async Task ImportAsync(IOctoSession session, ITenantCkModelRepository ckModelRepository, string filePath,
        CancellationToken? cancellationToken = null)
    {
        try
        {
            _logger.LogInformation("Reading CK model....");
            var operationResult = new OperationResult();
            await using var streamReader = File.OpenRead(filePath);
            var model = await _ckSerializer.DeserializeCompiledModelRootAsync(streamReader, operationResult);

            if (model == null || operationResult.HasErrors)
            {
                _logger.LogError("Import of CK model failed, model cannot be deserialized");
                operationResult.WriteMessagesToLogger(_logger);
                throw CommandExecutionFailedException.CannotDeserializeModel(filePath);
            }
            
            _logger.LogInformation("Executing import of CK model....");
            await _ckModelRepositoryManager.PublishModelAsync(InternalConstants.CkModelRepositoryName, model, false, ckModelRepository);

            _logger.LogInformation("Import of CK model completed");
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Import of CK model failed");
            throw;
        }
    }
}