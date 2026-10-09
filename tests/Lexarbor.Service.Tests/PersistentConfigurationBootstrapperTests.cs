using Lexarbor.Host;

namespace Lexarbor.Service.Tests;

public sealed class PersistentConfigurationBootstrapperTests : IDisposable
{
    private readonly string _contentRoot = Path.Combine(
        Path.GetTempPath(),
        $"lexarbor-config-{Guid.NewGuid():N}");

    [Fact]
    public void EnsureFile_MissingPersistentFile_CopiesImageTemplate()
    {
        Directory.CreateDirectory(_contentRoot);
        var expected = "{\"Database\":{\"InitializeOnStartup\":true}}";
        File.WriteAllText(
            Path.Combine(_contentRoot, PersistentConfigurationBootstrapper.FileName),
            expected);

        var result = PersistentConfigurationBootstrapper.EnsureFile(_contentRoot);

        Assert.True(result.Created);
        Assert.Equal(
            Path.Combine(_contentRoot, "data", PersistentConfigurationBootstrapper.FileName),
            result.Path);
        Assert.Equal(expected, File.ReadAllText(result.Path));
    }

    [Fact]
    public void EnsureFile_MissingDataDirectory_CreatesItOwnerOnly()
    {
        Directory.CreateDirectory(_contentRoot);
        File.WriteAllText(
            Path.Combine(_contentRoot, PersistentConfigurationBootstrapper.FileName),
            "image defaults");

        var result = PersistentConfigurationBootstrapper.EnsureFile(_contentRoot);

        Assert.True(result.Created);
        var dataDirectory = Path.Combine(_contentRoot, "data");
        Assert.True(Directory.Exists(dataDirectory));
        if (!OperatingSystem.IsWindows())
        {
            // The data directory is the parent of the Data Protection
            // root-key file, which the strict root-key source accepts only
            // under a directory with exactly the owner-only mode bits —
            // independent of the process umask the directory creation would
            // otherwise inherit.
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(dataDirectory));
        }
    }

    [Fact]
    public void EnsureFile_ExistingPersistentFile_DoesNotOverwriteIt()
    {
        Directory.CreateDirectory(Path.Combine(_contentRoot, "data"));
        File.WriteAllText(
            Path.Combine(_contentRoot, PersistentConfigurationBootstrapper.FileName),
            "image defaults");
        var persistentPath = Path.Combine(
            _contentRoot,
            "data",
            PersistentConfigurationBootstrapper.FileName);
        File.WriteAllText(persistentPath, "operator settings");

        var result = PersistentConfigurationBootstrapper.EnsureFile(_contentRoot);

        Assert.False(result.Created);
        Assert.Equal(persistentPath, result.Path);
        Assert.Equal("operator settings", File.ReadAllText(persistentPath));
    }

    [Fact]
    public void EnsureFile_MissingImageTemplate_FailsClearly()
    {
        Directory.CreateDirectory(_contentRoot);

        var action = () =>
            PersistentConfigurationBootstrapper.EnsureFile(_contentRoot);

        var exception = Assert.Throws<FileNotFoundException>(action);
        Assert.Contains("built-in appsettings.json template", exception.Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(_contentRoot))
        {
            Directory.Delete(_contentRoot, recursive: true);
        }
    }
}
