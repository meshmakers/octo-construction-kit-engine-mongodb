namespace Meshmakers.Octo.SystematizedData.CkModel.Contracts;

public class CompilerException : Exception
{
    public CompilerException(CompilerResult compilerResult) 
        : base("Compiler result contains errors")
    {
        CompilerResult = compilerResult;
    }

    public CompilerException(string message, CompilerResult compilerResult) : base(message)
    {
        CompilerResult = compilerResult;
    }

    public CompilerException(string message, Exception inner, CompilerResult compilerResult) : base(message, inner)
    {
        CompilerResult = compilerResult;
    }

    public CompilerResult CompilerResult { get; }

    public static Exception CompilerResultWithErrors(CompilerResult compilerResult)
    {
        return new CompilerException(compilerResult);
    }
    
    public static Exception DirectoryMustBeEmpty(string rootPath)
    {
        return new CompilerException($"Directory '{rootPath}' must be empty", new CompilerResult());
    }
    
}
