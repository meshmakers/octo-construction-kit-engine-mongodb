using Meshmakers.Octo.SystematizedData.CkModel.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.SystematizedData.CkModel.Compiler.Validation;

public interface ICkModelValidator
{
    Task<ValidationResult> ValidateAsync(CkModelRoot model);
}