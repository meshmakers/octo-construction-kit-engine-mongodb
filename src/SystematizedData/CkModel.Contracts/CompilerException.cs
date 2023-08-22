namespace Meshmakers.Octo.SystematizedData.CkModel.Contracts;

public class CompilerException : Exception
{
    public CompilerException(OperationResult operationResult) 
        : base("Compiler result contains errors")
    {
        OperationResult = operationResult;
    }

    public CompilerException(string message, OperationResult operationResult) : base(message)
    {
        OperationResult = operationResult;
    }

    public CompilerException(string message, Exception inner, OperationResult operationResult) : base(message, inner)
    {
        OperationResult = operationResult;
    }

    public OperationResult OperationResult { get; }

    public static Exception OperationResultWithErrors(OperationResult operationResult)
    {
        return new CompilerException(operationResult);
    }
    
    public static Exception DirectoryMustBeEmpty(string rootPath)
    {
        return new CompilerException($"Directory '{rootPath}' must be empty", new OperationResult());
    }
    
}
