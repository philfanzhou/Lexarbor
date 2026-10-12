using Lexarbor.Host.Configuration;

namespace Lexarbor.Host;

public static class PersistentConfigurationBootstrapper
{
    public const string FileName = "appsettings.json";

    public static bool IsRunningInContainer()
    {
        return string.Equals(
            Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"),
            "true",
            StringComparison.OrdinalIgnoreCase);
    }

    public static PersistentConfigurationFile EnsureFile(string contentRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);
        var store = new PersistentConfigurationFileStore(contentRootPath);
        var existing = store.Read();
        if (existing.Document is not null)
        {
            return new PersistentConfigurationFile(store.FilePath, false);
        }
        var templatePath = Path.Combine(contentRootPath, FileName);
        if (!File.Exists(templatePath))
        {
            throw new FileNotFoundException("The built-in appsettings.json template was not found.", templatePath);
        }
        store.ReadTemplate();
        try
        {
            // Preserve the image template bytes and create the credential file at 0600.
            store.CommitBytesAsync(File.ReadAllBytes(templatePath), CancellationToken.None, overwrite: false)
                .GetAwaiter().GetResult();
            return new PersistentConfigurationFile(store.FilePath, true);
        }
        catch (IOException) when (store.Read().Document is not null)
        {
            // Another bootstrapper won the create-only rename. Do not overwrite it.
            return new PersistentConfigurationFile(store.FilePath, false);
        }
    }
}

public sealed record PersistentConfigurationFile(string Path, bool Created);
