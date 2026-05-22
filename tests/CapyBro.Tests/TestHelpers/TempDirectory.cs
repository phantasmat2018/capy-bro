using System.IO;

namespace CapyBro.Tests.TestHelpers;

public sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        RootPath = Path.Combine(
            Path.GetTempPath(),
            "aitext-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootPath);
    }

    public string RootPath { get; }

    public string GetPath(string fileName) => Path.Combine(RootPath, fileName);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
        catch (UnauthorizedAccessException)
        {
            // best-effort cleanup
        }
    }
}
