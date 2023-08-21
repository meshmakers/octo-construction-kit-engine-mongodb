using Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.SystematizedData.CkModel.Contracts.Validation;

public interface ICkModelValidator
{
    Task<ValidationResult> ValidateAsync(CkCompiledModelRoot compiledModel);
}