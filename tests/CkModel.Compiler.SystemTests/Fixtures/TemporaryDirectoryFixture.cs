namespace CkModel.Compiler.SystemTests.Fixtures;

public class TemporaryDirectoryFixture : ServiceCollectionFixture, IDisposable
{
    public string TempDirectoryPath { get; }

    public TemporaryDirectoryFixture()
    {
        TempDirectoryPath = Path.Combine(Path.GetTempPath(), "CkModelCompilerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(TempDirectoryPath);
    }

    public void Dispose()
    {
        Directory.Delete(TempDirectoryPath, true);
    }
}