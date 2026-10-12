using System.Diagnostics;
using System.Text.Json.Nodes;
using Lexarbor.Host.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Lexarbor.Service.Tests;

public sealed class PersistentConfigurationCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "configuration-tests", $"lexarbor-save-{Guid.NewGuid():N}");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private string DataPath => Path.Combine(_root, "data");
    private string FilePath => Path.Combine(DataPath, "appsettings.json");

    public PersistentConfigurationCoordinatorTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task MissingFile_ReadDoesNotCreate_SaveUsesTemplateWithoutEnvironmentSecrets()
    {
        File.WriteAllText(Path.Combine(_root, "appsettings.json"), "{\"Unknown\":{\"Value\":7},\"Audio\":{\"SecretKey\":\"synthetic-file-value\"}}");
        var coordinator = Create(new Dictionary<string, string?> { ["AdminAuthentication:OidcCode:ClientSecret"] = "synthetic-environment-value" });
        var current = await coordinator.ReadAsync(Ct);
        Assert.False(Directory.Exists(DataPath));
        Assert.True(current.StorageAvailable);
        Assert.Equal(ConfigurationValueSource.Default, current.Sources["Audio:Enabled"].Source);

        var result = await coordinator.SaveAsync(current.Revision, Switch("Audio:Enabled", true), Prepare, Ct);

        Assert.Equal(ConfigurationSaveStatus.Success, result.Status);
        var saved = JsonNode.Parse(File.ReadAllText(FilePath))!;
        Assert.Equal(7, saved["Unknown"]!["Value"]!.GetValue<int>());
        Assert.Equal("synthetic-file-value", saved["Audio"]!["SecretKey"]!.GetValue<string>());
        Assert.Null(saved["AdminAuthentication"]);
        Assert.Equal(ConfigurationValueSource.File, (await coordinator.ReadAsync(Ct)).Sources["Audio:Enabled"].Source);
        Assert.NotEqual(current.Revision, result.Revision);
        AssertOwnerOnly();
    }

    [Fact]
    public async Task MissingTemplateAndDirectory_SaveUsesEmptyObject()
    {
        var coordinator = Create();
        var result = await coordinator.SaveAsync((await coordinator.ReadAsync(Ct)).Revision, Switch("Audio:Enabled", false), Prepare, Ct);
        Assert.Equal(ConfigurationSaveStatus.Success, result.Status);
        Assert.Single(JsonNode.Parse(File.ReadAllText(FilePath))!.AsObject());
        AssertOwnerOnly();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileOrDirectoryLost_RestoresLastCompleteSnapshotIncludingCredentials(bool loseDirectory)
    {
        Seed("{\"Unknown\": [1,2],\"Audio\":{\"SecretKey\":\"synthetic-retained\",\"Bucket\":\"saved-bucket\"}}");
        var coordinator = Create();
        var first = await coordinator.SaveAsync((await coordinator.ReadAsync(Ct)).Revision, Switch("RateLimits:AdminLogin:Enabled", true), Prepare, Ct);
        if (loseDirectory) Directory.Delete(DataPath, recursive: true);
        else File.Delete(FilePath);
        var publishCount = 0;
        var result = await coordinator.SaveAsync(first.Revision, Switch("Audio:Enabled", false), (configuration, _) =>
        {
            Assert.True(configuration.GetValue<bool>("RateLimits:AdminLogin:Enabled"));
            Assert.Equal("synthetic-retained", configuration["Audio:SecretKey"]);
            return Task.FromResult<PreparedConfigurationChange?>(new(() => publishCount++));
        }, Ct);
        Assert.Equal(ConfigurationSaveStatus.Success, result.Status);
        Assert.Equal(1, publishCount);
        var json = JsonNode.Parse(File.ReadAllText(FilePath))!;
        Assert.True(json["RateLimits"]!["AdminLogin"]!["Enabled"]!.GetValue<bool>());
        Assert.Equal("synthetic-retained", json["Audio"]!["SecretKey"]!.GetValue<string>());
        Assert.Equal("saved-bucket", json["Audio"]!["Bucket"]!.GetValue<string>());
        Assert.Equal(2, json["Unknown"]!.AsArray().Count);
    }

    [Fact]
    public async Task CrossModuleConcurrentSaves_OnlyOnePublishesAndOtherConflicts()
    {
        Seed("{\"Unknown\":{\"Keep\":true}}");
        var coordinator = Create();
        var revision = (await coordinator.ReadAsync(Ct)).Revision;
        var published = 0;
        Task<PreparedConfigurationChange?> Candidate(IConfiguration _, CancellationToken __) =>
            Task.FromResult<PreparedConfigurationChange?>(new(() => published++));
        var results = await Task.WhenAll(
            coordinator.SaveAsync(revision, Switch("Audio:Enabled", true), Candidate, Ct),
            coordinator.SaveAsync(revision, Switch("RateLimits:PublicApi:Enabled", true), Candidate, Ct));
        Assert.Single(results, result => result.Status == ConfigurationSaveStatus.Success);
        Assert.Single(results, result => result.Status == ConfigurationSaveStatus.Conflict);
        Assert.Equal(1, published);
        Assert.True(JsonNode.Parse(File.ReadAllText(FilePath))!["Unknown"]!["Keep"]!.GetValue<bool>());
    }

    [Fact]
    public async Task ExternalEdit_IsPreservedAndRequiresRestartEvenAfterRefresh()
    {
        Seed("{}");
        var coordinator = Create();
        var revision = (await coordinator.ReadAsync(Ct)).Revision;
        var edited = "{\"OperatorChange\":true}";
        File.WriteAllText(FilePath, edited);
        Assert.Equal(ConfigurationSaveStatus.Conflict, (await coordinator.SaveAsync(revision, Switch("Audio:Enabled", true), Prepare, Ct)).Status);
        var refreshed = await coordinator.ReadAsync(Ct);
        Assert.NotEqual(revision, refreshed.Revision);
        Assert.Equal(ConfigurationSaveStatus.Conflict, (await coordinator.SaveAsync(refreshed.Revision, Switch("Audio:Enabled", true), Prepare, Ct)).Status);
        Assert.Equal(edited, File.ReadAllText(FilePath));
        var restarted = Create();
        Assert.Equal(ConfigurationSaveStatus.Success, (await restarted.SaveAsync((await restarted.ReadAsync(Ct)).Revision, Switch("Audio:Enabled", true), Prepare, Ct)).Status);
    }

    [Fact]
    public async Task ExternalEditDuringPreparation_DiscardsCandidateWithoutOverwriting()
    {
        Seed("{}");
        var coordinator = Create();
        var discarded = false;
        var result = await coordinator.SaveAsync((await coordinator.ReadAsync(Ct)).Revision, Switch("Audio:Enabled", true), (_, _) =>
        {
            File.WriteAllText(FilePath, "{\"OperatorChange\":true}");
            return Task.FromResult<PreparedConfigurationChange?>(new(() => Assert.Fail("must not publish"), () => discarded = true));
        }, Ct);
        Assert.Equal(ConfigurationSaveStatus.Conflict, result.Status);
        Assert.True(discarded);
        Assert.True(JsonNode.Parse(File.ReadAllText(FilePath))!["OperatorChange"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData(null, ConfigurationSaveStatus.MissingRevision)]
    [InlineData("", ConfigurationSaveStatus.InvalidRevision)]
    [InlineData("*", ConfigurationSaveStatus.InvalidRevision)]
    [InlineData("W/\"abcdef\"", ConfigurationSaveStatus.InvalidRevision)]
    [InlineData("\"00000000000000000000000000000000\"", ConfigurationSaveStatus.Conflict)]
    public async Task MissingInvalidOrStaleRevision_DoesNotWrite(string? revision, ConfigurationSaveStatus expected)
    {
        var coordinator = Create();
        Assert.Equal(expected, (await coordinator.SaveAsync(revision, Switch("Audio:Enabled", true), Prepare, Ct)).Status);
        Assert.False(File.Exists(FilePath));
    }

    [Theory]
    [InlineData("Authentication:Secret", "true")]
    [InlineData("Audio:Enabled", "\"true\"")]
    [InlineData("Audio:SecretKey", "1")]
    [InlineData("Audio:Enabled", "null")]
    [InlineData("Audio:Bucket", "{}")]
    public async Task UnknownFieldOrWrongType_DoesNotPrepareOrWrite(string field, string json)
    {
        var coordinator = Create();
        var fields = new Dictionary<string, JsonNode?> { [field] = JsonNode.Parse(json) };
        var result = await coordinator.SaveAsync((await coordinator.ReadAsync(Ct)).Revision, fields,
            (_, _) => throw new InvalidOperationException("must not prepare"), Ct);
        Assert.Equal(ConfigurationSaveStatus.InvalidCandidate, result.Status);
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public async Task InvalidCandidate_DoesNotWriteOrPublish()
    {
        Seed("{}");
        var coordinator = Create();
        var result = await coordinator.SaveAsync((await coordinator.ReadAsync(Ct)).Revision, Switch("Audio:Enabled", true),
            (_, _) => throw new ArgumentException("synthetic-sensitive-value"), Ct);
        Assert.Equal(ConfigurationSaveStatus.InvalidCandidate, result.Status);
        Assert.Equal("{}", File.ReadAllText(FilePath));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{broken")]
    [InlineData("{\"Audio\":{},\"audio\":{}}")]
    [InlineData("{\"A\":{\"B\":1},\"A:B\":2}")]
    [InlineData("{\"A\":1,\"A:B\":2}")]
    [InlineData("{\"A\":{\"B\":{}},\"A:B\":{}}")]
    public async Task DamagedOrConflictingFile_ReturnsSafeFailureWithoutReplacing(string damaged)
    {
        Seed("{}");
        var coordinator = Create();
        var revision = (await coordinator.ReadAsync(Ct)).Revision;
        File.WriteAllText(FilePath, damaged);
        var result = await coordinator.SaveAsync(revision, Switch("Audio:Enabled", true), Prepare, Ct);
        Assert.Equal(ConfigurationSaveStatus.StorageFailure, result.Status);
        Assert.False((await coordinator.ReadAsync(Ct)).StorageAvailable);
        Assert.Equal(damaged, File.ReadAllText(FilePath));
        var exception = Assert.Throws<IOException>(() => new PersistentConfigurationCoordinator(new ConfigurationBuilder().Build(), new(_root)));
        Assert.Equal(ConfigurationSaveResult.StorageFailureMessage, exception.Message);
    }

    [Fact]
    public async Task EnvironmentAndCommandLineSources_RejectOnlyCoveredFields()
    {
        Seed("{\"Audio\":{\"Enabled\":false}}");
        var prefix = $"LEXARBOR_TEST_{Guid.NewGuid():N}_";
        Environment.SetEnvironmentVariable(prefix + "Audio__Enabled", "true");
        try
        {
            var configuration = Builder().AddEnvironmentVariables(prefix)
                .AddCommandLine(["--RateLimits:AdminLogin:Enabled=true"]).Build();
            var coordinator = new PersistentConfigurationCoordinator(configuration, new(_root));
            var read = await coordinator.ReadAsync(Ct);
            Assert.Equal(new ConfigurationFieldSource(ConfigurationValueSource.Environment, false), read.Sources["Audio:Enabled"]);
            Assert.Equal(new ConfigurationFieldSource(ConfigurationValueSource.CommandLine, false), read.Sources["RateLimits:AdminLogin:Enabled"]);
            var blocked = await coordinator.SaveAsync(read.Revision, Switch("Audio:Enabled", false), Prepare, Ct);
            Assert.Equal(ConfigurationSaveStatus.Overridden, blocked.Status);
            Assert.Equal(["Audio:Enabled"], blocked.OverriddenFields);
            var saved = await coordinator.SaveAsync(read.Revision, Switch("RateLimits:PublicApi:Enabled", true), (candidate, _) =>
            {
                Assert.True(candidate.GetValue<bool>("Audio:Enabled"));
                Assert.True(candidate.GetValue<bool>("RateLimits:AdminLogin:Enabled"));
                return Prepare(candidate, Ct);
            }, Ct);
            Assert.Equal(ConfigurationSaveStatus.Success, saved.Status);
            Assert.False(JsonNode.Parse(File.ReadAllText(FilePath))!["Audio"]!["Enabled"]!.GetValue<bool>());
        }
        finally { Environment.SetEnvironmentVariable(prefix + "Audio__Enabled", null); }
    }

    [Theory]
    [InlineData("file")]
    [InlineData("directory")]
    [InlineData("dangling")]
    public async Task SymbolicLinkTargets_AreRejectedAndReferentIsUnchanged(string kind)
    {
        if (OperatingSystem.IsWindows()) return;
        Seed("{}");
        var coordinator = Create();
        var revision = (await coordinator.ReadAsync(Ct)).Revision;
        var referent = Path.Combine(_root, "referent");
        File.WriteAllText(referent, "synthetic-referent");
        if (kind == "directory")
        {
            Directory.Move(DataPath, DataPath + "-original");
            Directory.CreateSymbolicLink(DataPath, DataPath + "-original");
        }
        else
        {
            File.Delete(FilePath);
            File.CreateSymbolicLink(FilePath, kind == "dangling" ? referent + "-missing" : referent);
        }
        Assert.Equal(ConfigurationSaveStatus.StorageFailure, (await coordinator.SaveAsync(revision, Switch("Audio:Enabled", true), Prepare, Ct)).Status);
        Assert.Equal("synthetic-referent", File.ReadAllText(referent));
    }

    [Theory]
    [InlineData("write")]
    [InlineData("replace")]
    [InlineData("cancel-before")]
    [InlineData("cancel-after")]
    public async Task FailureAndCancellation_RespectCommitPointAndCleanTemporaryFiles(string phase)
    {
        Seed("{}");
        using var cancellation = new CancellationTokenSource();
        var store = new FaultStore(_root, phase, cancellation);
        var coordinator = new PersistentConfigurationCoordinator(Builder().Build(), store);
        var revision = (await coordinator.ReadAsync(Ct)).Revision;
        var published = false;
        var discarded = false;
        var save = () => coordinator.SaveAsync(revision, Switch("Audio:Enabled", true), (_, _) =>
            Task.FromResult<PreparedConfigurationChange?>(new(() => published = true, () => discarded = true)), cancellation.Token);
        if (phase == "cancel-before")
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(save);
        }
        else
        {
            var result = await save();
            Assert.Equal(phase == "cancel-after" ? ConfigurationSaveStatus.Success : ConfigurationSaveStatus.StorageFailure, result.Status);
        }
        Assert.Equal(phase == "cancel-after", published);
        Assert.Equal(phase != "cancel-after", discarded);
        Assert.Empty(Directory.GetFiles(DataPath, "*.tmp"));
        if (phase == "cancel-after") Assert.True(JsonNode.Parse(File.ReadAllText(FilePath))!["Audio"]!["Enabled"]!.GetValue<bool>());
        else Assert.Equal("{}", File.ReadAllText(FilePath));
    }

    [Fact]
    public async Task ReadOnlyDirectory_CanReadAndFailedSaveKeepsRuntimeAndFile()
    {
        if (OperatingSystem.IsWindows()) return;
        Seed("{}");
        File.SetUnixFileMode(DataPath, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var coordinator = Create();
            var read = await coordinator.ReadAsync(Ct);
            Assert.True(read.StorageAvailable);
            var result = await coordinator.SaveAsync(read.Revision, Switch("Audio:Enabled", true), (_, _) =>
                Task.FromResult<PreparedConfigurationChange?>(new(() => Assert.Fail("must not publish"))), Ct);
            Assert.Equal(ConfigurationSaveStatus.StorageFailure, result.Status);
            Assert.Equal("{}", File.ReadAllText(FilePath));
        }
        finally { File.SetUnixFileMode(DataPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    [Fact]
    public async Task SavePublishesOnlyModuleCandidate_StartupConfigurationIsNotReloaded()
    {
        Seed("{\"Authentication\":{\"Authority\":\"startup-only\"},\"Audio\":{\"Enabled\":false}}");
        var startup = Builder().Build();
        var coordinator = new PersistentConfigurationCoordinator(startup, new(_root));
        var result = await coordinator.SaveAsync((await coordinator.ReadAsync(Ct)).Revision, Switch("Audio:Enabled", true), Prepare, Ct);
        Assert.Equal(ConfigurationSaveStatus.Success, result.Status);
        Assert.False(startup.GetValue<bool>("Audio:Enabled"));
        var restarted = Builder().Build();
        Assert.True(restarted.GetValue<bool>("Audio:Enabled"));
        Assert.Equal("startup-only", restarted["Authentication:Authority"]);
        Assert.False(startup.Providers.OfType<Microsoft.Extensions.Configuration.Json.JsonConfigurationProvider>().Single().Source.ReloadOnChange);
    }

    [Theory]
    [InlineData("before")]
    [InlineData("after")]
    public async Task ProcessExitAroundAtomicReplacement_RestartsFromCompleteOldOrNewFile(string phase)
    {
        Seed("{\"OldMarker\":true}");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(typeof(PersistentConfigurationCoordinatorTests).Assembly.Location);
        start.ArgumentList.Add("--filter-method");
        start.ArgumentList.Add("Lexarbor.Service.Tests.PersistentConfigurationCoordinatorTests.CrashChild");
        start.Environment["LEXARBOR_CONFIG_CRASH_ROOT"] = _root;
        start.Environment["LEXARBOR_CONFIG_CRASH_PHASE"] = phase;
        using var child = Process.Start(start)!;
        var output = child.StandardOutput.ReadToEndAsync(Ct);
        var error = child.StandardError.ReadToEndAsync(Ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await child.WaitForExitAsync(timeout.Token);
        await Task.WhenAll(output, error);
        Assert.Equal(phase == "before" ? 72 : 73, child.ExitCode);
        var saved = JsonNode.Parse(File.ReadAllText(FilePath))!;
        Assert.True(saved["OldMarker"]!.GetValue<bool>());
        Assert.Equal(phase == "after", saved["Audio"]?["Enabled"]?.GetValue<bool>() ?? false);
        // An interrupted rename can leave a flushed temporary file, which startup ignores.
        if (phase == "before") Assert.Single(Directory.GetFiles(DataPath, "*.tmp"));
        var restarted = Create();
        Assert.True((await restarted.ReadAsync(Ct)).StorageAvailable);
        Assert.Equal(phase == "after", Builder().Build().GetValue<bool>("Audio:Enabled"));
    }

    [Fact]
    public async Task CrashChild()
    {
        var root = Environment.GetEnvironmentVariable("LEXARBOR_CONFIG_CRASH_ROOT");
        if (root is null) return;
        var configuration = new ConfigurationBuilder().AddJsonFile(Path.Combine(root, "data", "appsettings.json")).Build();
        var store = new CrashStore(root, Environment.GetEnvironmentVariable("LEXARBOR_CONFIG_CRASH_PHASE")!);
        var coordinator = new PersistentConfigurationCoordinator(configuration, store);
        await coordinator.SaveAsync((await coordinator.ReadAsync(Ct)).Revision, Switch("Audio:Enabled", true), Prepare, Ct);
        Assert.Fail("child must exit at the requested commit point");
    }

    private sealed class CrashStore(string root, string phase) : PersistentConfigurationFileStore(root)
    {
        protected override void Replace(string temporaryPath, bool overwrite)
        {
            if (phase == "before") Environment.Exit(72);
            base.Replace(temporaryPath, overwrite);
            Environment.Exit(73);
        }
    }

    [Theory]
    [InlineData("{\"audio\":{\"Enabled\":false,\"Other\":1}}")]
    [InlineData("{\"Audio:Enabled\":false,\"Audio:Other\":1}")]
    [InlineData("{\"Audio\":{\"Enabled\":false},\"Audio:Other\":1}")]
    [InlineData("{/* operator comment */\"Audio\":{\"Enabled\":false,\"Other\":1,},}")]
    public async Task CaseInsensitiveAndColonDelimitedKeys_AreUpdatedWithoutDuplicateKeys(string original)
    {
        Seed(original);
        var coordinator = Create();
        var read = await coordinator.ReadAsync(Ct);
        Assert.Equal(ConfigurationValueSource.File, read.Sources["Audio:Enabled"].Source);
        Assert.Equal(ConfigurationSaveStatus.Success,
            (await coordinator.SaveAsync(read.Revision, Switch("Audio:Enabled", true), Prepare, Ct)).Status);
        var restarted = Builder().Build();
        Assert.True(restarted.GetValue<bool>("Audio:Enabled"));
        Assert.Equal(1, restarted.GetValue<int>("Audio:Other"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostStartup_LoadsOptionalDataFileAndDoesNotGenerateMissingFile(bool existing)
    {
        if (existing) Seed("{\"StartupProbe\":\"from-data-file\",\"Audio\":{\"Enabled\":false}}");
        using var factory = new TestInfrastructure.VocabularyWebApplicationFactory("Testing", true, contentRootPath: _root);
        var configuration = factory.Services.GetRequiredService<IConfiguration>();
        Assert.Equal(existing ? "from-data-file" : null, configuration["StartupProbe"]);
        Assert.Equal(existing, File.Exists(FilePath));
        var coordinator = factory.Services.GetRequiredService<PersistentConfigurationCoordinator>();
        Assert.True((await coordinator.ReadAsync(Ct)).StorageAvailable);
        Assert.Equal(existing ? ConfigurationValueSource.File : ConfigurationValueSource.Default,
            (await coordinator.ReadAsync(Ct)).Sources["Audio:Enabled"].Source);
    }

    private sealed class FaultStore(string root, string phase, CancellationTokenSource cancellation) : PersistentConfigurationFileStore(root)
    {
        protected override async Task WriteTemporaryAsync(string path, byte[] bytes, CancellationToken cancellationToken)
        {
            await base.WriteTemporaryAsync(path, bytes, cancellationToken);
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
            if (phase == "write") throw new IOException("synthetic-write-failure");
            if (phase == "cancel-before") cancellation.Cancel();
        }
        protected override void Replace(string temporaryPath, bool overwrite)
        {
            if (phase == "replace") throw new IOException("synthetic-replace-failure");
            base.Replace(temporaryPath, overwrite);
            if (phase == "cancel-after") cancellation.Cancel();
        }
    }

    private PersistentConfigurationCoordinator Create(Dictionary<string, string?>? defaults = null) =>
        new(Builder(defaults).Build(), new(_root));
    private IConfigurationBuilder Builder(Dictionary<string, string?>? defaults = null) => new ConfigurationBuilder()
        .AddInMemoryCollection(defaults ?? []).AddJsonFile(FilePath, optional: true, reloadOnChange: false);
    private static Dictionary<string, JsonNode?> Switch(string field, bool value) => new() { [field] = JsonValue.Create(value) };
    private static Task<PreparedConfigurationChange?> Prepare(IConfiguration _, CancellationToken __) => Task.FromResult<PreparedConfigurationChange?>(new(() => { }));
    private void Seed(string json) { Directory.CreateDirectory(DataPath); File.WriteAllText(FilePath, json); }
    private void AssertOwnerOnly()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(DataPath));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(FilePath));
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
