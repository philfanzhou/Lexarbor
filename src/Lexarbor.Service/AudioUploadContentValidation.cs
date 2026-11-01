namespace Lexarbor.Service;

// Shared by multipart HTTP and background publication. The automatic minimum
// is additional policy; manual uploads keep their original MP3 range.
public static class AudioUploadContentValidation
{
    public static bool IsValid(byte[] bytes, long maxBytes, bool generated = false) =>
        bytes.LongLength <= maxBytes && (!generated || bytes.Length >= 300) && Mp3Validation.IsValid(bytes);
}
