using System.Reflection;
using System.Reflection.Emit;
using Lexarbor.Host;

namespace Lexarbor.Service.Tests;

public class ApplicationVersionTests
{
    private const string Revision = "0123456789abcdef0123456789abcdef01234567";

    [Theory]
    [InlineData(null, "unknown")]
    [InlineData("", "unknown")]
    [InlineData(" \t", "unknown")]
    [InlineData("+sdk-revision", "unknown")]
    [InlineData("1.2.3+sdk-revision", "1.2.3")]
    [InlineData("1.3.0-rc.1+sdk-revision", "1.3.0-rc.1")]
    [InlineData("0.0.0-dev", "0.0.0-dev")]
    public void Version_PreservesPrereleaseAndFallsBackIndependently(string? version, string expected)
    {
        var identity = ApplicationVersion.Read(CreateAssembly(version,
            ("BuildRevision", Revision), ("BuildChannel", "release")));

        Assert.Equal(expected, identity.Version);
        Assert.Equal(Revision, identity.Revision);
        Assert.Equal("release", identity.Channel);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("0123456")]
    [InlineData("0123456789abcdef0123456789abcdef012345678")]
    [InlineData("0123456789abcdef0123456789abcdef01234567890")]
    [InlineData("g123456789abcdef0123456789abcdef01234567")]
    [InlineData(" 123456789abcdef0123456789abcdef01234567")]
    public void Revision_InvalidOrEmpty_IsNull(string? revision)
    {
        var identity = ApplicationVersion.Read(CreateAssembly("1.2.3",
            ("BuildRevision", revision), ("BuildChannel", "edge")));

        Assert.Null(identity.Revision);
        Assert.Equal("1.2.3", identity.Version);
        Assert.Equal("edge", identity.Channel);
    }

    [Theory]
    [InlineData("release", "release")]
    [InlineData("edge", "edge")]
    [InlineData("development", "development")]
    [InlineData(null, "development")]
    [InlineData("", "development")]
    [InlineData(" ", "development")]
    [InlineData("Release", "development")]
    [InlineData("preview", "development")]
    public void Channel_UsesOnlyExplicitKnownValues(string? channel, string expected)
    {
        var identity = ApplicationVersion.Read(CreateAssembly("0.0.0-dev",
            ("BuildRevision", Revision.ToUpperInvariant()), ("BuildChannel", channel)));

        Assert.Equal(expected, identity.Channel);
        Assert.Equal(Revision, identity.Revision);
    }

    [Fact]
    public void MissingMetadata_DoesNotInferIdentityFromVersionOrSdkSuffix()
    {
        var identity = ApplicationVersion.Read(CreateAssembly($"1.2.3+{Revision}"));

        Assert.Equal("1.2.3", identity.Version);
        Assert.Null(identity.Revision);
        Assert.Equal("development", identity.Channel);
    }

    [Theory]
    [InlineData("BuildRevision")]
    [InlineData("BuildChannel")]
    public void DuplicateMetadata_InvalidatesOnlyThatKey(string duplicateKey)
    {
        var duplicateValue = duplicateKey == "BuildRevision" ? Revision : "release";
        var identity = ApplicationVersion.Read(CreateAssembly("1.3.0-rc.1",
            ("BuildRevision", Revision), ("BuildChannel", "release"),
            (duplicateKey, duplicateValue), ("Unrelated", "a"), ("Unrelated", "b")));

        Assert.Equal("1.3.0-rc.1", identity.Version);
        Assert.Equal(duplicateKey == "BuildRevision" ? null : Revision, identity.Revision);
        Assert.Equal(duplicateKey == "BuildChannel" ? "development" : "release", identity.Channel);
    }

    [Fact]
    public void RuntimeSnapshot_ReadsHostAssemblyInsteadOfTestRunner()
    {
        var expected = ApplicationVersion.Read(typeof(Program).Assembly);

        Assert.NotSame(typeof(Program).Assembly, typeof(ApplicationVersionTests).Assembly);
        Assert.Equal(expected.Version, ApplicationVersion.Current);
        Assert.Equal(expected.Revision, ApplicationVersion.Revision);
        Assert.Equal(expected.Channel, ApplicationVersion.Channel);
    }

    private static Assembly CreateAssembly(string? version, params (string Key, string? Value)[] metadata)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName($"BuildIdentityTest_{Guid.NewGuid():N}"), AssemblyBuilderAccess.RunAndCollect);
        if (version != null)
        {
            assembly.SetCustomAttribute(new CustomAttributeBuilder(
                typeof(AssemblyInformationalVersionAttribute).GetConstructor([typeof(string)])!, [version]));
        }

        foreach (var (key, value) in metadata)
        {
            assembly.SetCustomAttribute(new CustomAttributeBuilder(
                typeof(AssemblyMetadataAttribute).GetConstructor([typeof(string), typeof(string)])!, [key, value!]));
        }

        return assembly;
    }
}
