namespace Lexarbor.Host.Audio;

// Never retain SDK exceptions: their messages and inner exceptions can contain
// credentials, signed URLs and internal endpoints.
public sealed class AudioStorageException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
