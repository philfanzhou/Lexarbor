using FluentAssertions;
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

        result.Created.Should().BeTrue();
        result.Path.Should().Be(Path.Combine(
            _contentRoot,
            "data",
            PersistentConfigurationBootstrapper.FileName));
        File.ReadAllText(result.Path).Should().Be(expected);
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

        result.Created.Should().BeFalse();
        result.Path.Should().Be(persistentPath);
        File.ReadAllText(persistentPath).Should().Be("operator settings");
    }

    [Fact]
    public void EnsureFile_MissingImageTemplate_FailsClearly()
    {
        Directory.CreateDirectory(_contentRoot);

        var action = () =>
            PersistentConfigurationBootstrapper.EnsureFile(_contentRoot);

        action.Should().Throw<FileNotFoundException>()
            .WithMessage("*built-in appsettings.json template*");
    }

    public void Dispose()
    {
        if (Directory.Exists(_contentRoot))
        {
            Directory.Delete(_contentRoot, recursive: true);
        }
    }
}
