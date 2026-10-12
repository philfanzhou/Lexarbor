using Lexarbor.Host;

namespace Lexarbor.Service.Tests;

public sealed class PersistentConfigurationBootstrapperTests : IDisposable
{
    private readonly string _contentRoot = Path.Combine(
        AppContext.BaseDirectory,
        "configuration-tests",
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
            "{\"ImageDefaults\":true}");

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
            "{\"ImageDefaults\":true}");
        var persistentPath = Path.Combine(
            _contentRoot,
            "data",
            PersistentConfigurationBootstrapper.FileName);
        File.WriteAllText(persistentPath, "{\"OperatorSettings\":true}");

        var result = PersistentConfigurationBootstrapper.EnsureFile(_contentRoot);

        Assert.False(result.Created);
        Assert.Equal(persistentPath, result.Path);
        Assert.Equal("{\"OperatorSettings\":true}", File.ReadAllText(persistentPath));
    }

    [Theory]
    [InlineData("utf-8")]
    [InlineData("utf-16-le")]
    [InlineData("utf-16-be")]
    [InlineData("utf-32-le")]
    [InlineData("utf-32-be")]
    public void EnsureFile_BomTemplateAndExistingFile_PreservesBytes(string encoding)
    {
        Directory.CreateDirectory(_contentRoot);
        var bytes = PersistentConfigurationCoordinatorTests.EncodeWithBom("{\"Unknown\":\"保留🌳\"}", encoding);
        File.WriteAllBytes(Path.Combine(_contentRoot, PersistentConfigurationBootstrapper.FileName), bytes);

        var created = PersistentConfigurationBootstrapper.EnsureFile(_contentRoot);
        Assert.True(created.Created);
        Assert.Equal(bytes, File.ReadAllBytes(created.Path));
        File.WriteAllText(Path.Combine(_contentRoot, PersistentConfigurationBootstrapper.FileName), "{}");

        var existing = PersistentConfigurationBootstrapper.EnsureFile(_contentRoot);
        Assert.False(existing.Created);
        Assert.Equal(bytes, File.ReadAllBytes(existing.Path));
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
