using Meshmakers.Octo.Common.Shared.Exchange;

namespace Meshmakers.Octo.SystematizedData.CkModel.Compiler.Validation;

public interface ICkModelValidator
{
    Task<ValidationResult> ValidateAsync(CkModelRoot model);
}