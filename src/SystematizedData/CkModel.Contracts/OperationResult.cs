using Meshmakers.Octo.SystematizedData.CkModel.Contracts.Messages;

namespace Meshmakers.Octo.SystematizedData.CkModel.Contracts;

public class OperationResult
{
    public OperationResult()
    {
        Messages = new();
    }

    public List<CompilerMessage> Messages { get; }

    public bool HasErrors => Messages.Any(x => x.MessageLevel == MessageLevel.Error);
    public bool HasFatalErrors => Messages.Any(x => x.MessageLevel == MessageLevel.FatalError);

    public void AddMessage(CompilerMessage message)
    {
        Messages.Add(message);
    }
}