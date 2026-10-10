namespace Bonfire.Tests;

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = Directory.CreateTempSubdirectory("bonfire-tests-").FullName;
    }

    public string Path { get; }

    public void Dispose() => Directory.Delete(Path, recursive: true);
}
