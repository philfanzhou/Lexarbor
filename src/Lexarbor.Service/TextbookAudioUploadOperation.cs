using Lexarbor.Domain.Services;

namespace Lexarbor.Service;

public sealed record TextbookAudioUploadHttpSettings(bool Enabled, long MaxBytes, int MaxDurationSeconds, int TimeoutSeconds);
public interface ITextbookAudioUploadOperationFactory
{
    Task<TextbookAudioUploadRequestOperation> AcquireAsync(CancellationToken cancellationToken);
}
public sealed class TextbookAudioUploadRequestOperation(TextbookAudioUploadHttpSettings settings,
    TextbookAudioUploadService service, IDisposable reference) : IDisposable
{
    public TextbookAudioUploadHttpSettings Settings { get; } = settings;
    public TextbookAudioUploadService Service { get; } = service;
    public void Dispose() => reference.Dispose();
}
