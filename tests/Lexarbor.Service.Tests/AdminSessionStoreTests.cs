using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Net;
using System.Text.Json;
using Lexarbor.Database;
using Lexarbor.Database.Entities;
using Lexarbor.Database.Repositories;
using Lexarbor.Domain.Exceptions;
using Lexarbor.Domain.Repositories;
using Lexarbor.Host;
using Lexarbor.Host.Authentication;
using Lexarbor.Service.Tests.TestInfrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Lexarbor.Service.Tests;

public sealed class AdminSessionStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"lexarbor-sessions-{Guid.NewGuid():N}");
    private readonly Clock _clock = new();
    private string DatabasePath => Path.Combine(_root, "sessions.db");
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private ValidatedAdminSession Session(DateTimeOffset? expiry = null) => new()
    {
        AccessToken = "synthetic-access-marker",
        IdToken = "synthetic-id-marker",
        Issuer = "synthetic-issuer-marker",
        Subject = "synthetic-subject-marker",
        DisplayName = "synthetic-display-marker",
        Roles = ["synthetic-role-marker"],
        AccessTokenExpiresAt = expiry ?? _clock.GetUtcNow().AddMinutes(5)
    };

    private ServiceProvider Build(string? ringRoot = null, IInterceptor? interceptor = null, bool migrate = true)
    {
        Directory.CreateDirectory(_root);
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.None));
        PersistentAdminKeyRing.Register(services);
        services.RemoveAll<PersistentAdminKeyRing>();
        services.AddSingleton(new PersistentAdminKeyRing(ringRoot ?? _root));
        services.AddSingleton<TimeProvider>(_clock);
        services.AddDbContext<VocabularyDbContext>(options =>
        {
            options.UseSqlite($"Data Source={DatabasePath};Pooling=False;Default Timeout=1");
            if (interceptor is not null) options.AddInterceptors(interceptor);
        });
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<AdminSessionRepository>();
        services.AddScoped<AdminSessionStore>();
        var provider = services.BuildServiceProvider();
        PersistentAdminKeyRing.Validate(provider);
        if (migrate)
        {
            using var scope = provider.CreateScope();
            scope.ServiceProvider.GetRequiredService<VocabularyDbContext>().Database.Migrate();
        }
        return provider;
    }

    private static AdminSessionStore Store(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<AdminSessionStore>();
    private static VocabularyDbContext Db(IServiceScope scope) => scope.ServiceProvider.GetRequiredService<VocabularyDbContext>();
    private static string Hash(string handle) => Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(handle)));

    [Fact]
    public async Task Restart_PreservesSessionWithSameRing_AndDatabaseHasNoPlaintext()
    {
        string handle;
        using (var first = Build())
        using (var scope = first.CreateScope())
        {
            handle = await Store(scope).CreateAsync(Session(), Ct);
            var row = await Db(scope).AdminSessions.SingleAsync(Ct);
            Assert.True(row.HandleHash == Hash(handle));
            Assert.NotEqual(handle, row.HandleHash);
            Assert.Equal(_clock.GetUtcNow().AddMinutes(5).ToUnixTimeMilliseconds(), row.ExpiresAtUnixMs);
        }
        using var second = Build();
        using var secondScope = second.CreateScope();
        var recovered = await Store(secondScope).ReadAsync(handle, Ct);
        Assert.NotNull(recovered);
        Assert.True(recovered.AccessToken == Session().AccessToken && recovered.Subject == Session().Subject);
        // Check bytes and values without printing the markers, handle or ciphertext on failure.
        var database = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(DatabasePath, Ct));
        Assert.True(!database.Contains(handle, StringComparison.Ordinal));
        foreach (var marker in new[] { Session().AccessToken, Session().IdToken!, Session().Issuer,
                     Session().Subject, Session().DisplayName!, Session().Roles[0] })
            Assert.True(!database.Contains(marker, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ActualHosts_RestartWithFileDatabaseAndPersistentRing()
    {
        Directory.CreateDirectory(_root);
        string handle;
        using (var first = new VocabularyWebApplicationFactory("Testing", true, keyContentRoot: _root,
                   databasePath: DatabasePath))
        {
            using var client = first.CreateClient();
            using var scope = first.Services.CreateScope();
            handle = await Store(scope).CreateAsync(Session(DateTimeOffset.UtcNow.AddMinutes(5)), Ct);
        }
        using var second = new VocabularyWebApplicationFactory("Testing", true, keyContentRoot: _root,
            databasePath: DatabasePath);
        using var secondClient = second.CreateClient();
        using var secondScope = second.Services.CreateScope();
        Assert.NotNull(await Store(secondScope).ReadAsync(handle, Ct));
        Assert.True((await secondClient.GetAsync("/health", Ct)).IsSuccessStatusCode);
    }

    [Fact]
    public async Task Expiry_IsExactAndNeverSlidesWithoutCleanup()
    {
        using var provider = Build();
        using var scope = provider.CreateScope();
        var expiry = _clock.GetUtcNow().AddMilliseconds(1234);
        var handle = await Store(scope).CreateAsync(Session(expiry), Ct);
        _clock.Now = expiry.AddMilliseconds(-1);
        Assert.NotNull(await Store(scope).ReadAsync(handle, Ct));
        _clock.Now = expiry;
        Assert.Null(await Store(scope).ReadAsync(handle, Ct));
        Assert.Equal(1, await Db(scope).AdminSessions.CountAsync(Ct));
        Assert.Null(await Store(scope).RevokeAndReadAsync(handle, Ct));
        Assert.Empty(await Db(scope).AdminSessions.ToListAsync(Ct));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB")]
    public async Task InvalidHandle_ReturnsNoSessionWithoutDatabaseAccess(string? handle)
    {
        using var provider = Build(migrate: false);
        using var scope = provider.CreateScope();
        Assert.Null(await Store(scope).ReadAsync(handle, Ct));
        Assert.Null(await Store(scope).RevokeAndReadAsync(handle, Ct));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("subject")]
    [InlineData("roles")]
    [InlineData("token")]
    [InlineData("hash")]
    [InlineData("expiry")]
    [InlineData("model")]
    [InlineData("ciphertext")]
    [InlineData("row-expiry")]
    [InlineData("swap")]
    public async Task Tampering_ProducesNoIdentity_AndRevokeDeletesEvenCorruptRecords(string change)
    {
        using var provider = Build();
        using var scope = provider.CreateScope();
        var first = await Store(scope).CreateAsync(Session(), Ct);
        var second = await Store(scope).CreateAsync(Session(), Ct);
        var row = await Db(scope).AdminSessions.SingleAsync(item => item.HandleHash == Hash(first), Ct);
        var protector = provider.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector(PersistentAdminKeyRing.AdminSessionPurpose);
        var model = JsonNode.Parse(protector.Unprotect(row.ProtectedPayload))!;
        switch (change)
        {
            case "version": model["Version"] = 9; break;
            case "subject": model["Session"]!["Subject"] = ""; break;
            case "roles": model["Session"]!["Roles"] = new JsonArray(" "); break;
            case "token": model["Session"]!["AccessToken"] = null; break;
            case "hash": model["HandleHash"] = Hash(second); break;
            case "expiry": model["ExpiresAtUnixMs"] = row.ExpiresAtUnixMs + 1; break;
            case "model": model = JsonNode.Parse("{\"Session\":false}")!; break;
            case "ciphertext": row.ProtectedPayload = "broken"; break;
            case "row-expiry": row.ExpiresAtUnixMs += 1000; break;
            case "swap":
                row.ProtectedPayload = (await Db(scope).AdminSessions.AsNoTracking()
                .SingleAsync(item => item.HandleHash == Hash(second), Ct)).ProtectedPayload; break;
        }
        if (change is not ("ciphertext" or "row-expiry" or "swap"))
            row.ProtectedPayload = protector.Protect(model.ToJsonString());
        await Db(scope).SaveChangesAsync(Ct);
        Assert.Null(await Store(scope).ReadAsync(first, Ct));
        Assert.Null(await Store(scope).RevokeAndReadAsync(first, Ct));
        Assert.False(await Db(scope).AdminSessions.AnyAsync(item => item.HandleHash == Hash(first), Ct));
        Assert.NotNull(await Store(scope).ReadAsync(second, Ct));
    }

    [Fact]
    public async Task RotationRetainsSession_MissingKeyAndWrongPurposeFailClosed()
    {
        string handle;
        using (var first = Build())
        using (var scope = first.CreateScope())
        {
            handle = await Store(scope).CreateAsync(Session(), Ct);
            first.GetRequiredService<IKeyManager>().CreateNewKey(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(90));
        }
        using var rotated = Build();
        using var rotatedScope = rotated.CreateScope();
        Assert.NotNull(await Store(rotatedScope).ReadAsync(handle, Ct));
        using var missing = Build(Path.Combine(_root, "missing"));
        using var missingScope = missing.CreateScope();
        Assert.Null(await Store(missingScope).ReadAsync(handle, Ct));
        var row = await Db(rotatedScope).AdminSessions.SingleAsync(Ct);
        var wrong = rotated.GetRequiredService<IDataProtectionProvider>().CreateProtector("wrong-purpose");
        row.ProtectedPayload = wrong.Protect("{}");
        await Db(rotatedScope).SaveChangesAsync(Ct);
        Assert.Null(await Store(rotatedScope).ReadAsync(handle, Ct));
    }

    [Fact]
    public async Task Replace_IsAtomicAndIndependentOfMissingOrExpiredOldHandle()
    {
        using var provider = Build();
        using var scope = provider.CreateScope();
        var old = await Store(scope).CreateAsync(Session(), Ct);
        var next = await Store(scope).ReplaceAsync(old, Session(_clock.GetUtcNow().AddMinutes(1)), Ct);
        Assert.Null(await Store(scope).ReadAsync(old, Ct));
        Assert.NotNull(await Store(scope).ReadAsync(next, Ct));
        Assert.Equal(_clock.GetUtcNow().AddMinutes(1), (await Store(scope).ReadAsync(next, Ct))!.AccessTokenExpiresAt);
        var fromMissing = await Store(scope).ReplaceAsync(old, Session(), Ct);
        Assert.NotNull(await Store(scope).ReadAsync(fromMissing, Ct));
        _clock.Now = _clock.Now.AddMinutes(1);
        var fromExpired = await Store(scope).ReplaceAsync(next, Session(), Ct);
        Assert.Null(await Store(scope).ReadAsync(next, Ct));
        Assert.NotNull(await Store(scope).ReadAsync(fromExpired, Ct));
    }

    [Fact]
    public async Task ConcurrentReplacements_CreateIndependentRows_AndNeverReviveOldHandle()
    {
        using var provider = Build();
        using var setup = provider.CreateScope();
        var old = await Store(setup).CreateAsync(Session(), Ct);
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();
        await using var gate = await WriteGate.OpenAsync(provider, Ct);
        var firstReplace = Store(first).ReplaceAsync(old, Session(), Ct);
        var secondReplace = Store(second).ReplaceAsync(old, Session(), Ct);
        Assert.False(firstReplace.IsCompleted || secondReplace.IsCompleted);
        await gate.ReleaseAsync();
        var handles = await Task.WhenAll(firstReplace, secondReplace);
        Assert.NotEqual(handles[0], handles[1]);
        Assert.Null(await Store(setup).ReadAsync(old, Ct));
        foreach (var handle in handles) Assert.NotNull(await Store(setup).ReadAsync(handle, Ct));
    }

    [Fact]
    public async Task DoubleRevoke_OneSnapshot_AndFreshReadCannotUseTrackedIdentity()
    {
        using var provider = Build();
        using var reader = provider.CreateScope();
        var handle = await Store(reader).CreateAsync(Session(), Ct);
        _ = await Db(reader).AdminSessions.SingleAsync(Ct); // deliberately stale EF tracking
        var earlierSnapshot = await Store(reader).ReadAsync(handle, Ct);
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();
        await using var gate = await WriteGate.OpenAsync(provider, Ct);
        var firstRevoke = Store(first).RevokeAndReadAsync(handle, Ct);
        var secondRevoke = Store(second).RevokeAndReadAsync(handle, Ct);
        Assert.False(firstRevoke.IsCompleted || secondRevoke.IsCompleted);
        await gate.ReleaseAsync();
        var revoked = await Task.WhenAll(firstRevoke, secondRevoke);
        Assert.Single(revoked, item => item is not null);
        Assert.Null(await Store(reader).ReadAsync(handle, Ct));
        Assert.NotNull(earlierSnapshot); // already-started request snapshot is not retroactively revoked
        Assert.Null(await Store(reader).RevokeAndReadAsync(handle, Ct));
    }

    [Fact]
    public async Task Cleanup_IsBounded_AndConcurrentReadRevokeNeverRevivesRecords()
    {
        using var provider = Build();
        using var scope = provider.CreateScope();
        var expiry = _clock.Now.AddSeconds(1);
        var handles = new List<string>();
        for (var index = 0; index < 105; index++) handles.Add(await Store(scope).CreateAsync(Session(expiry), Ct));
        var valid = await Store(scope).CreateAsync(Session(), Ct);
        _clock.Now = expiry;
        using var cleaner = provider.CreateScope();
        using var revoker = provider.CreateScope();
        using var reader = provider.CreateScope();
        await using var gate = await WriteGate.OpenAsync(provider, Ct);
        var read = Store(reader).ReadAsync(handles[0], Ct);
        var cleanup = Store(cleaner).CleanupAsync(Ct);
        var revoke = Store(revoker).RevokeAndReadAsync(handles[0], Ct);
        Assert.False(cleanup.IsCompleted || revoke.IsCompleted);
        await gate.ReleaseAsync();
        await Task.WhenAll(read, cleanup, revoke);
        Assert.Null(await read);
        Assert.Null(await revoke);
        Assert.Equal(AdminSessionRepository.CleanupBatchSize, await cleanup);
        var remaining = await Store(scope).CleanupAsync(Ct);
        Assert.InRange(remaining, 4, 5);
        Assert.NotNull(await Store(scope).ReadAsync(valid, Ct));
        Assert.Equal(1, await Db(scope).AdminSessions.CountAsync(Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replace_PrecommitFailureOrCancellation_RollsBackAndKeepsOldSession(bool cancel)
    {
        using var provider = Build();
        using var initial = provider.CreateScope();
        var old = await Store(initial).CreateAsync(Session(), Ct);
        using var cancellation = new CancellationTokenSource();
        using var failingProvider = Build(interceptor: new AfterInsert(() =>
        {
            if (cancel) cancellation.Cancel();
            else throw new SqliteException("synthetic-storage-secret", 1);
        }));
        using var failed = failingProvider.CreateScope();
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store(failed).ReplaceAsync(old, Session(), cancellation.Token));
        else
        {
            var error = await Assert.ThrowsAsync<AdminSessionStorageException>(() => Store(failed).ReplaceAsync(old, Session(), Ct));
            Assert.Equal(AdminSessionRepository.StorageFailureMessage, error.Message);
            Assert.Null(error.InnerException);
        }
        Assert.NotNull(await Store(initial).ReadAsync(old, Ct));
        Assert.Equal(1, await Db(initial).AdminSessions.CountAsync(Ct));
    }

    [Fact]
    public async Task Create_PrecommitCancellation_ReturnsNoHandleAndLeavesNoRow()
    {
        using var setup = Build();
        using var cancellation = new CancellationTokenSource();
        using var provider = Build(interceptor: new AfterInsert(() => cancellation.Cancel()), migrate: false);
        using var scope = provider.CreateScope();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store(scope).CreateAsync(Session(), cancellation.Token));
        Assert.Empty(await Db(scope).AdminSessions.ToListAsync(Ct));
    }

    [Fact]
    public async Task NestedSessionWrites_AreRejectedBeforeReturningUncommittedResults()
    {
        using var provider = Build();
        using var scope = provider.CreateScope();
        using var other = provider.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().ExecuteInTransactionAsync(async () =>
        {
            await Assert.ThrowsAsync<AdminSessionStorageException>(() => Store(scope).CreateAsync(Session(), Ct));
            await Assert.ThrowsAsync<AdminSessionStorageException>(() => Store(other).CreateAsync(Session(), Ct));
            return 0;
        }, Ct);
        Assert.Empty(await Db(scope).AdminSessions.ToListAsync(Ct));
    }

    [Fact]
    public async Task LostResultAfterCommit_IsUnknownAndDoesNotAutomaticallyCreateAgain()
    {
        using var setup = Build();
        using var provider = Build(interceptor: new AfterCommit(), migrate: false);
        using var scope = provider.CreateScope();
        await Assert.ThrowsAsync<AdminSessionStorageException>(() => Store(scope).CreateAsync(Session(), Ct));
        // The committed row exists, even though no handle could be delivered. Never replay Create.
        Assert.Equal(1, await Db(scope).AdminSessions.CountAsync(Ct));
    }

    [Fact]
    public async Task SessionWrites_ShareVocabularyLock_AndWaitingCancellationDoesNotRunAction()
    {
        using var provider = Build();
        using var vocabulary = provider.CreateScope();
        using var session = provider.CreateScope();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var write = vocabulary.ServiceProvider.GetRequiredService<IUnitOfWork>().ExecuteInTransactionAsync(async () =>
        {
            Db(vocabulary).Vocabularies.Add(new VocabularyEntity
            { Id = "lock-word", Word = "locked", CreatedAt = _clock.Now, UpdatedAt = _clock.Now });
            await vocabulary.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(Ct);
            entered.SetResult();
            await release.Task;
            return 0;
        }, Ct);
        await entered.Task.WaitAsync(Ct);
        using var cancelled = new CancellationTokenSource();
        var waiting = Store(session).CreateAsync(Session(), cancelled.Token);
        try
        {
            Assert.False(waiting.IsCompleted);
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        }
        finally { release.TrySetResult(); }
        await write;
        Assert.Empty(await Db(session).AdminSessions.ToListAsync(Ct));
        Assert.Equal(1, await Db(session).Vocabularies.CountAsync(Ct));
        using var preCancelled = new CancellationTokenSource();
        preCancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store(session).CreateAsync(Session(), preCancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store(session).CleanupAsync(preCancelled.Token));
    }

    [Fact]
    public async Task DatabaseErrors_AreSafeAndDifferentFromMissingRecords_AndFailedRevokeKeepsRow()
    {
        using var provider = Build();
        using var scope = provider.CreateScope();
        var handle = await Store(scope).CreateAsync(Session(), Ct);
        await using var blocker = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
        await blocker.OpenAsync(Ct);
        using var transaction = blocker.BeginTransaction();
        using var failed = provider.CreateScope();
        var busy = await Assert.ThrowsAsync<StorageBusyException>(() => Store(failed).RevokeAndReadAsync(handle, Ct));
        Assert.Equal(AdminSessionRepository.StorageFailureMessage, busy.Message);
        Assert.Null(busy.InnerException);
        transaction.Rollback();
        Assert.NotNull(await Store(scope).ReadAsync(handle, Ct));
        await Db(scope).Database.ExecuteSqlRawAsync("DROP TABLE admin_session", Ct);
        var error = await Assert.ThrowsAsync<AdminSessionStorageException>(() => Store(scope).ReadAsync(handle, Ct));
        Assert.Equal(AdminSessionRepository.StorageFailureMessage, error.Message);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task InvalidStructuresOrExpiredTrustedInput_AreRejectedWithoutRows()
    {
        using var provider = Build();
        using var scope = provider.CreateScope();
        foreach (var invalid in new[]
        {
            new ValidatedAdminSession(),
            new ValidatedAdminSession { AccessToken = "synthetic", Issuer = "issuer", Subject = "sub", Roles = [""], AccessTokenExpiresAt = _clock.Now.AddMinutes(1) },
            Session(_clock.Now)
        }) await Assert.ThrowsAsync<ArgumentException>(() => Store(scope).CreateAsync(invalid, Ct));
        Assert.Empty(await Db(scope).AdminSessions.ToListAsync(Ct));
    }

    [Fact]
    public async Task CleanupFailure_IsSafeAndRetryable_AndNoInitializationHostStaysHealthy()
    {
        using var provider = Build(migrate: false);
        var logger = new CapturingLogger();
        using var service = new AdminSessionCleanupService(provider.GetRequiredService<IServiceScopeFactory>(), _clock, logger);
        await service.RunBatchAsync(Ct);
        Assert.Equal([AdminSessionCleanupService.FailureDiagnostic], logger.Messages);
        using (var setup = provider.CreateScope()) await Db(setup).Database.MigrateAsync(Ct);
        await service.RunBatchAsync(Ct);
        Assert.Single(logger.Messages);
        using var host = new VocabularyWebApplicationFactory("Testing", true,
            extraConfiguration: new Dictionary<string, string?> { ["Database:InitializeOnStartup"] = "false" });
        using var client = host.CreateClient();
        using var hostCleanup = new AdminSessionCleanupService(host.Services.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System, logger);
        await hostCleanup.RunBatchAsync(Ct);
        // With initialization disabled and the shared database still unmigrated,
        // readiness honestly reports not-ready; the host itself stays alive and
        // serving, which liveness now expresses separately from readiness.
        using var alive = await client.GetAsync("/health/live", Ct);
        Assert.True(alive.IsSuccessStatusCode);
        using var ready = await client.GetAsync("/health/ready", Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal("notStarted", JsonDocument.Parse(await ready.Content.ReadAsStringAsync(Ct))
            .RootElement.GetProperty("migrationStatus").GetString());
    }

    [Fact]
    public async Task HostCleanupFailure_LogsOnlySafeDiagnosticWithoutSqlOrProviderException()
    {
        var logs = new RecordingLogs();
        using var host = new VocabularyWebApplicationFactory("Testing", true,
            extraConfiguration: new Dictionary<string, string?> { ["Database:InitializeOnStartup"] = "false" });
        using var configured = host.WithWebHostBuilder(builder => builder.ConfigureLogging(logging => logging.AddProvider(logs)));
        using var client = configured.CreateClient();
        logs.Entries.Clear();
        using var cleanup = new AdminSessionCleanupService(configured.Services.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System, configured.Services.GetRequiredService<ILogger<AdminSessionCleanupService>>());
        await cleanup.RunBatchAsync(Ct);
        Assert.True(logs.Entries.Count == 1 && logs.Entries[0].Message == AdminSessionCleanupService.FailureDiagnostic
            && !logs.Entries[0].HasException);
    }

    private sealed class RecordingLogs : ILoggerProvider
    {
        public List<(string Message, bool HasException)> Entries { get; } = [];
        public ILogger CreateLogger(string categoryName) => new Recorder(this);
        public void Dispose() { }
        private sealed class Recorder(RecordingLogs owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => owner.Entries.Add((formatter(state, exception), exception is not null));
        }
    }

    private sealed class WriteGate(IServiceScope scope, Task transaction, TaskCompletionSource release) : IAsyncDisposable
    {
        public static async Task<WriteGate> OpenAsync(IServiceProvider provider, CancellationToken cancellationToken)
        {
            var scope = provider.CreateScope();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var transaction = scope.ServiceProvider.GetRequiredService<IUnitOfWork>().ExecuteInTransactionAsync(async () =>
            {
                entered.SetResult();
                await release.Task;
                return 0;
            }, cancellationToken);
            await entered.Task.WaitAsync(cancellationToken);
            return new WriteGate(scope, transaction, release);
        }
        public async Task ReleaseAsync()
        {
            release.TrySetResult();
            await transaction;
        }
        public async ValueTask DisposeAsync()
        {
            await ReleaseAsync();
            scope.Dispose();
        }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 30, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class AfterInsert(Action afterInsert) : DbCommandInterceptor
    {
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("INSERT INTO admin_session", StringComparison.Ordinal)) afterInsert();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class AfterCommit : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default) => throw new SqliteException("synthetic-lost-result", 1);
    }

    private sealed class CapturingLogger : ILogger<AdminSessionCleanupService>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Assert.Null(exception);
            Messages.Add(formatter(state, exception));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
