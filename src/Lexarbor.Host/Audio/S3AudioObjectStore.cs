using Lexarbor.Domain.Models;
using Lexarbor.Domain.Repositories;
using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;

namespace Lexarbor.Host.Audio;

// Never retain SDK exceptions: their messages and inner exceptions can contain
// credentials, signed URLs and internal endpoints.
public sealed class AudioStorageException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class S3AudioObjectStore : IAudioObjectStore, IDisposable
{
    private readonly AudioOptions _options;
    private readonly TimeProvider _time;
    private readonly IMinioClient? _internal;
    private readonly IMinioClient? _public;
    private readonly string _publicPrefix = "";

    public S3AudioObjectStore(IOptions<AudioOptions> options, TimeProvider time)
    {
        _options = options.Value;
        _time = time;
        if (!_options.IsValid()) throw new InvalidOperationException(AudioOptions.ConfigurationFailure);
        if (!_options.Enabled) return;
        var internalUri = new Uri(_options.InternalEndpoint);
        var publicUri = new Uri(_options.PublicEndpoint);
        _publicPrefix = publicUri.AbsolutePath.TrimEnd('/');
        _internal = CreateClient(internalUri);
        _public = CreateClient(new Uri(publicUri.GetLeftPart(UriPartial.Authority)));
    }

    private IMinioClient CreateClient(Uri endpoint) => new MinioClient()
        .WithEndpoint(endpoint.Host, endpoint.Port)
        .WithSSL(endpoint.Scheme == "https")
        .WithCredentials(_options.AccessKey, _options.SecretKey)
        .WithRegion(_options.Region)
        .WithTimeout(_options.OperationTimeoutSeconds * 1000)
        // A dedicated client avoids HttpClientFactory request logging of signed URLs.
        .WithHttpClient(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }), true)
        .Build();

    public Task PutAsync(AudioObjectLocation location, Stream content, long byteLength, string contentType,
        CancellationToken cancellationToken)
    {
        ValidateLocation(location);
        if (byteLength < 1 || byteLength > _options.MaxUploadBytes || contentType != "audio/mpeg" || !content.CanRead)
            throw new AudioStorageException("audio.storage.invalid_input", "Audio object input is invalid.");
        return RunAsync(async token =>
        {
            await _internal!.PutObjectAsync(new PutObjectArgs().WithBucket(location.Bucket).WithObject(location.Key)
                .WithStreamData(content).WithObjectSize(byteLength).WithContentType(contentType), token);
            return true;
        }, cancellationToken);
    }

    public Task<AudioObjectInfo?> StatAsync(AudioObjectLocation location, CancellationToken cancellationToken)
    {
        ValidateLocation(location);
        return RunAsync<AudioObjectInfo?>(async token =>
        {
            try
            {
                var stat = await _internal!.StatObjectAsync(new StatObjectArgs().WithBucket(location.Bucket)
                    .WithObject(location.Key), token);
                // ETag is deliberately not used as a SHA-256 hash.
                return new AudioObjectInfo(stat.Size, stat.ContentType);
            }
            catch (ObjectNotFoundException) { return null; }
        }, cancellationToken);
    }

    public Task RemoveAsync(AudioObjectLocation location, CancellationToken cancellationToken)
    {
        ValidateLocation(location);
        return RunAsync(async token =>
        {
            await _internal!.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(location.Bucket).WithObject(location.Key), token);
            return true;
        }, cancellationToken);
    }

    public Task<AudioPlaybackLink> GetPlaybackLinkAsync(AudioObjectLocation location, TimeSpan validity,
        CancellationToken cancellationToken)
    {
        ValidateLocation(location);
        if (validity.TotalSeconds is < 60 or > 3600 || validity.TotalSeconds != Math.Truncate(validity.TotalSeconds))
            throw new AudioStorageException("audio.storage.invalid_input", "Audio link validity is invalid.");
        return RunAsync(async token =>
        {
            token.ThrowIfCancellationRequested();
            var now = _time.GetUtcNow();
            // Region is explicit, so signing never contacts the public endpoint.
            var signed = await _public!.PresignedGetObjectAsync(new PresignedGetObjectArgs()
                .WithBucket(location.Bucket).WithObject(location.Key).WithExpiry((int)validity.TotalSeconds)
                .WithRequestDate(now.UtcDateTime));
            var uri = new Uri(signed);
            var url = uri.GetLeftPart(UriPartial.Authority) + _publicPrefix + uri.PathAndQuery;
            return new AudioPlaybackLink(url, now.Add(validity));
        }, cancellationToken);
    }

    private void ValidateLocation(AudioObjectLocation location)
    {
        if (!_options.Enabled) throw new AudioStorageException("audio.storage.disabled", "Audio storage is disabled.");
        if (location.Bucket != _options.Bucket || !location.Key.StartsWith(_options.KeyPrefix, StringComparison.Ordinal))
            throw new AudioStorageException("audio.storage.invalid_location", "Audio object location is invalid.");
        var suffix = location.Key[_options.KeyPrefix.Length..];
        if (suffix.Length != 36 || !suffix.EndsWith(".mp3", StringComparison.Ordinal)
            || !Guid.TryParseExact(suffix[..32], "N", out _) || suffix[..32] != suffix[..32].ToLowerInvariant())
            throw new AudioStorageException("audio.storage.invalid_location", "Audio object location is invalid.");
    }

    private async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.OperationTimeoutSeconds));
        try
        {
            var result = await operation(timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception) when (timeout.IsCancellationRequested)
        {
            throw new AudioStorageException("audio.storage.timeout", "Audio storage operation timed out.");
        }
        catch (Exception)
        {
            throw new AudioStorageException("audio.storage.unavailable", "Audio storage is unavailable.");
        }
    }

    public void Dispose()
    {
        _internal?.Dispose();
        _public?.Dispose();
    }
}
