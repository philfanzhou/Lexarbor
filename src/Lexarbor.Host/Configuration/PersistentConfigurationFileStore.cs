using System.Text.Json;
using System.Text.Json.Nodes;

namespace Lexarbor.Host.Configuration;

/// <summary>Fixed server-owned path; never accepts a path from a settings request.</summary>
public class PersistentConfigurationFileStore
{
    private const UnixFileMode DirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private readonly string _contentRoot;

    public PersistentConfigurationFileStore(string contentRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRoot);
        _contentRoot = Path.GetFullPath(contentRoot);
        DirectoryPath = Path.Combine(_contentRoot, "data");
        FilePath = Path.Combine(DirectoryPath, PersistentConfigurationBootstrapper.FileName);
    }

    public string DirectoryPath { get; }
    public string FilePath { get; }

    public ConfigurationFileSnapshot Read()
    {
        ValidatePaths();
        return File.Exists(FilePath)
            ? ReadDocument(FilePath)
            : new ConfigurationFileSnapshot(null, null);
    }

    public JsonObject ReadTemplate()
    {
        var path = Path.Combine(_contentRoot, PersistentConfigurationBootstrapper.FileName);
        RejectLink(path);
        return File.Exists(path) ? ReadDocument(path).Document! : new JsonObject();
    }

    public async Task CommitAsync(JsonObject document, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, new JsonSerializerOptions { WriteIndented = true });
        await CommitBytesAsync(bytes, cancellationToken);
    }

    public async Task CommitBytesAsync(byte[] bytes, CancellationToken cancellationToken, bool overwrite = true)
    {
        // Validate before touching a directory; a damaged file is never reset to defaults.
        Parse(bytes);
        ValidatePaths();
        RejectReadOnlyTarget();
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(DirectoryPath);
        }
        else
        {
            Directory.CreateDirectory(DirectoryPath, DirectoryMode);
        }
        ValidatePaths();
        var temporaryPath = Path.Combine(DirectoryPath, $".appsettings.json.{Guid.NewGuid():N}.tmp");
        try
        {
            await WriteTemporaryAsync(temporaryPath, bytes, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ValidatePaths();
            RejectReadOnlyTarget();
            Replace(temporaryPath, overwrite);
            // No cancellation or fallible work after the commit point. The caller must
            // publish its already prepared runtime candidate even if the client disconnects.
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    protected virtual async Task WriteTemporaryAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var options = new FileStreamOptions
        {
            Mode = System.IO.FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.WriteThrough
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = FileMode;
        }
        await using var stream = new FileStream(path, options);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        stream.Flush(flushToDisk: true);
    }

    protected virtual void Replace(string temporaryPath, bool overwrite)
    {
        File.Move(temporaryPath, FilePath, overwrite);
    }

    private void RejectReadOnlyTarget()
    {
        // Atomic rename can otherwise replace a read-only file on Unix because
        // directory write permission is sufficient. Honor the operator's file protection.
        if (File.Exists(FilePath) && (File.GetAttributes(FilePath) & FileAttributes.ReadOnly) != 0)
            throw new UnauthorizedAccessException(ConfigurationSaveResult.StorageFailureMessage);
    }

    private void ValidatePaths()
    {
        for (var directory = new DirectoryInfo(_contentRoot); directory is not null; directory = directory.Parent)
        {
            RejectLink(directory.FullName);
        }
        RejectLink(DirectoryPath);
        RejectLink(FilePath);
        if (File.Exists(DirectoryPath) || Directory.Exists(FilePath))
        {
            throw new IOException(ConfigurationSaveResult.StorageFailureMessage);
        }
    }

    private static void RejectLink(string path)
    {
        // LinkTarget also detects dangling links, which File.Exists does not.
        if (new FileInfo(path).LinkTarget is not null || new DirectoryInfo(path).LinkTarget is not null
            || (File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException(ConfigurationSaveResult.StorageFailureMessage);
        }
    }

    private static ConfigurationFileSnapshot ReadDocument(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return new ConfigurationFileSnapshot(Parse(bytes), bytes);
    }

    public static JsonObject Parse(byte[] bytes)
    {
        try
        {
            // Match the JSON configuration provider's BOM detection and text decoding.
            // ReadDocument keeps the original bytes for disk-change comparisons.
            using var stream = new MemoryStream(bytes);
            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
            using var document = JsonDocument.Parse(json, options);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException();
            }
            var leaves = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Visit(document.RootElement, "", leaves);
            return JsonNode.Parse(json, documentOptions: options)!.AsObject();
        }
        catch (JsonException)
        {
            // Never return a parser error containing a credential or supplied value.
            throw new IOException(ConfigurationSaveResult.StorageFailureMessage);
        }
    }

    private static void Visit(JsonElement element, string path, HashSet<string> keys)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (!element.EnumerateObject().Any())
            {
                AddKey(path, keys);
            }
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Length == 0 || !names.Add(property.Name))
                {
                    throw new JsonException();
                }
                Visit(property.Value, path.Length == 0 ? property.Name : $"{path}:{property.Name}", keys);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            if (element.GetArrayLength() == 0)
            {
                AddKey(path, keys);
            }
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                Visit(item, $"{path}:{index++}", keys);
            }
        }
        else
        {
            AddKey(path, keys);
        }
    }

    private static void AddKey(string path, HashSet<string> keys)
    {
        if (!keys.Add(path) || keys.Any(key => !key.Equals(path, StringComparison.OrdinalIgnoreCase)
                && (key.StartsWith(path + ":", StringComparison.OrdinalIgnoreCase)
                    || path.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase))))
        {
            throw new JsonException();
        }
    }
}

public sealed record ConfigurationFileSnapshot(JsonObject? Document, byte[]? Bytes);
