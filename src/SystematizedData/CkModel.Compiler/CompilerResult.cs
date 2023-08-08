using Meshmakers.Octo.SystematizedData.CkModel.Compiler.Messages;

namespace Meshmakers.Octo.SystematizedData.CkModel.Compiler;

public class CompilerResult
{
    public CompilerResult()
    {
        Messages = new();
    }

    public List<CompilerMessage> Messages { get; }

    internal void AddMessage(CompilerMessage message)
    {
        Messages.Add(message);
    }
}