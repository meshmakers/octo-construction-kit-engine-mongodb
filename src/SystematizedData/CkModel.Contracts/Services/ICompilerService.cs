namespace Meshmakers.Octo.SystematizedData.CkModel.Contracts.Services;

public interface ICompilerService
{
    Task CreateNewAsync(string rootPath);
    Task CompileAsync(string rootPath);
}