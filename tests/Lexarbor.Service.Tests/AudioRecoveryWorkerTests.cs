using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Lexarbor.Domain.Services;
using Lexarbor.Host.Audio;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Lexarbor.Service.Tests;

public sealed class AudioRecoveryWorkerTests
{
    [Fact]
    public async Task DisabledWorker_DoesNotResolveRepositoryOrStorage()
    {
        var services = new ServiceCollection();
        services.AddScoped<AudioRecoveryService>(_ => throw new InvalidOperationException("Must not resolve disabled recovery."));
        using var provider = services.BuildServiceProvider();
        using var worker = new AudioRecoveryWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new AudioOptions()), TimeProvider.System, Mock.Of<ILogger<AudioRecoveryWorker>>());
        await worker.StartAsync(TestContext.Current.CancellationToken);
        await worker.ExecuteTask!;
        await worker.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task BatchFailure_DoesNotFailHostOrBusyLoopOrLogProviderDetails()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var count = 0;
        var repository = new Mock<IAudioUploadRepository>();
        repository.Setup(repo => repo.FindRecoverableAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(() => { Interlocked.Increment(ref count); entered.TrySetResult(); throw new InvalidOperationException("synthetic-secret-url-marker"); });
        var services = new ServiceCollection(); services.AddScoped(_ => new AudioRecoveryService(repository.Object,
            Mock.Of<IAudioObjectStore>(), new(true, "test-audio", "audio/"), TimeProvider.System));
        using var provider = services.BuildServiceProvider(); var logger = new Mock<ILogger<AudioRecoveryWorker>>();
        using var worker = new AudioRecoveryWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new AudioOptions { Enabled = true, RecoveryIntervalSeconds = 60 }), TimeProvider.System, logger.Object);
        await worker.StartAsync(TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(1, count); Assert.False(worker.ExecuteTask!.IsCompleted);
        await worker.StopAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal(1, count);
        Assert.All(logger.Invocations, call => { Assert.Null(call.Arguments[3]); Assert.DoesNotContain("synthetic-secret-url-marker", call.Arguments[2]?.ToString() ?? ""); });
    }
}
