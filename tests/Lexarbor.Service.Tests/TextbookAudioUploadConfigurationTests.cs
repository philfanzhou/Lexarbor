using System.Text.Json;
using Lexarbor.Domain.Repositories;
using Lexarbor.Host.Audio;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Moq;

namespace Lexarbor.Service.Tests;

public sealed class TextbookAudioUploadConfigurationTests
{
    private static AudioOptions Options() => new()
    {
        Enabled = true,
        InternalEndpoint = "http://127.0.0.1:9/",
        PublicEndpoint = "https://audio.example.test/",
        AccessKey = "synthetic",
        SecretKey = "synthetic"
    };
    [Fact]
    public void DefaultsAndInclusiveLimits_PreserveWordLimitAndCopySnapshot()
    {
        var options = Options(); Assert.True(options.IsValid());
        Assert.Equal(64 * 1024 * 1024, options.MaxTextbookUploadBytes); Assert.Equal(7200, options.MaxTextbookDurationSeconds);
        Assert.Equal(5 * 1024 * 1024, options.MaxUploadBytes);
        options.MaxTextbookUploadBytes = 1; options.MaxTextbookDurationSeconds = 1; Assert.True(options.IsValid());
        options.MaxTextbookUploadBytes = 128 * 1024 * 1024; options.MaxTextbookDurationSeconds = 14400; Assert.True(options.IsValid());
        var copy = options.Copy(); var snapshot = AudioRuntimeSnapshot.From(copy, 7);
        Assert.Equal(options.MaxTextbookUploadBytes, copy.MaxTextbookUploadBytes); Assert.Equal(options.MaxTextbookDurationSeconds, copy.MaxTextbookDurationSeconds);
        Assert.Equal(copy.MaxTextbookUploadBytes, snapshot.MaxTextbookUploadBytes); Assert.Equal(copy.MaxTextbookDurationSeconds, snapshot.MaxTextbookDurationSeconds);
    }
    [Theory]
    [InlineData(true, 0, 7200)]
    [InlineData(false, 0, 7200)]
    [InlineData(true, 134217729, 7200)]
    [InlineData(false, 134217729, 7200)]
    [InlineData(true, 67108864, 0)]
    [InlineData(false, 67108864, 0)]
    [InlineData(true, 67108864, 14401)]
    [InlineData(false, 67108864, 14401)]
    public void NewLimits_MustRemainBoundedEvenWhileDisabled(bool enabled, long bytes, int seconds)
    { var options = Options(); options.Enabled = enabled; options.MaxTextbookUploadBytes = bytes; options.MaxTextbookDurationSeconds = seconds; Assert.False(options.IsValid()); }
    [Theory]
    [InlineData("bytes")]
    [InlineData("seconds")]
    public async Task OnlineChangesToNewLimits_RequireRestart(string field)
    {
        var options = Options(); var factory = new Mock<IAudioObjectStoreFactory>(); factory.Setup(f => f.Create(It.IsAny<AudioOptions>())).Returns(Mock.Of<IAudioObjectStore>());
        using var runtime = new AudioRuntimeConfiguration(Microsoft.Extensions.Options.Options.Create(options), factory.Object);
        var candidate = options.Copy(); if (field == "bytes") candidate.MaxTextbookUploadBytes--; else candidate.MaxTextbookDurationSeconds--;
        using var input = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { Audio = candidate }));
        var configuration = new ConfigurationBuilder().AddJsonStream(input).Build();
        Assert.Null(await runtime.PrepareAsync(configuration, TestContext.Current.CancellationToken));
        using var current = runtime.Acquire(); Assert.Equal(options.MaxTextbookUploadBytes, current.Snapshot.MaxTextbookUploadBytes); Assert.Equal(options.MaxTextbookDurationSeconds, current.Snapshot.MaxTextbookDurationSeconds);
    }
}
