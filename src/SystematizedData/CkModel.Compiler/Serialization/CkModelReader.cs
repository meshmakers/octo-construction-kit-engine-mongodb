using Meshmakers.Octo.SystematizedData.CkModel.Compiler.Validation;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.Serialization;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.Validation;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.SystematizedData.CkModel.Compiler.Serialization;

public class CkModelReader
{
    private readonly ICkModelValidator _ckModelValidator;
    private readonly ILogger<CkModelReader> _logger;
    private readonly ICkSerializer _ckSerializer;

    public CkModelReader(ILogger<CkModelReader> logger, ICkSerializer ckSerializer, ICkModelValidator ckModelValidator)
    {
        _ckModelValidator = ckModelValidator;
        _logger = logger;
        _ckSerializer = ckSerializer;
    }

    public async Task ReadAsync(string filePath, CancellationToken? cancellationToken = null)
    {
        _logger.LogInformation("Reading CK model...");

        CkModelRoot? model;

        try
        {
            using (var streamReader = new StreamReader(filePath))
            {
                model = await _ckSerializer.DeserializeModelRootAsync(streamReader);
            }

            if (model == null)
            {
                throw ModelParseException.CannotDeserializeModel(filePath);
            }
        }
        catch (Exception e)
        {
            throw ModelParseException.CommonErrorReadCkModel(filePath, e);
        }

        _logger.LogInformation("Validating CK model...");
        await _ckModelValidator.ValidateAsync(model);

        
    }
}