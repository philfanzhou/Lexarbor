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
