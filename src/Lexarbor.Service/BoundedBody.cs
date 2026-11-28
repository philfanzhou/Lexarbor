using Microsoft.AspNetCore.Http;

namespace Lexarbor.Service;

/// <summary>
/// Reads a JSON request body of at most <paramref name="maxBytes"/> bytes. A longer body is
/// refused as 413 the same way whether its length was announced or only discovered while reading.
/// </summary>
internal static class BoundedBody
{
    public static async Task<byte[]> ReadAsync(HttpRequest request, int maxBytes, CancellationToken cancellationToken)
    {
        if (request.ContentLength > maxBytes) throw TooLarge();
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (bytes.Length + read > maxBytes) throw TooLarge();
            bytes.Write(buffer, 0, read);
        }
        return bytes.ToArray();
    }

    private static BadHttpRequestException TooLarge() => new("The request body is too large.", StatusCodes.Status413PayloadTooLarge);
}
