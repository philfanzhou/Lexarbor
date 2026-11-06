using Lexarbor.Host;

namespace Lexarbor.Service.Tests;

public sealed class SpeechPreviewAdmissionTests
{
    [Fact]
    public void PreviewAdmission_BoundsConcurrencyAndMinuteBudget_AndReleasesOnce()
    {
        var clock = new Clock(); var admission = new SpeechPreviewAdmission(clock);
        var first = admission.Acquire(out _); Assert.NotNull(first);
        Assert.Null(admission.Acquire(out var retry)); Assert.Equal(1, retry);
        first.Dispose(); first.Dispose();
        for (var i = 0; i < 5; i++) { using var lease = admission.Acquire(out _); Assert.NotNull(lease); }
        Assert.Null(admission.Acquire(out retry)); Assert.Equal(60, retry);
        clock.Now = clock.Now.AddSeconds(59);
        Assert.Null(admission.Acquire(out retry)); Assert.Equal(1, retry);
        clock.Now = clock.Now.AddSeconds(1);
        using var next = admission.Acquire(out _); Assert.NotNull(next);
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
