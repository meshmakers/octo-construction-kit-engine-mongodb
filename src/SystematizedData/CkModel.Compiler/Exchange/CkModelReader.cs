using Meshmakers.Octo.SystematizedData.CkModel.Compiler.Validation;
using Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Common.Shared.Exchange;

public class CkModelReader
{
    private readonly ICkModelValidator _ckModelValidator;
    private readonly ILogger<CkModelReader> _logger;

    public CkModelReader(ILogger<CkModelReader> logger, ICkModelValidator ckModelValidator)
    {
        _ckModelValidator = ckModelValidator;
        _logger = logger;
    }

    public async Task ReadAsync(string filePath, CancellationToken? cancellationToken = null)
    {
        _logger.LogInformation("Reading CK model...");

        CkModelRoot? model;

        try
        {
            using (var streamReader = new StreamReader(filePath))
            {
                model = await CkSerializer.DeserializeAsync(streamReader);
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