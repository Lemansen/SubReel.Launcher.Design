namespace SubReel.Launcher.Tests;

/// <summary>Временная папка теста; удаляется вместе с тестом.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Root = Path.Combine(Path.GetTempPath(), "subreel-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string File(string name) => Path.Combine(Root, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // уборка временной папки не должна ронять тест
        }
    }
}
