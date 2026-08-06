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

        var templatePath = Path.Combine(contentRootPath, FileName);
        if (!File.Exists(templatePath))
        {
            throw new FileNotFoundException(
                "The built-in appsettings.json template was not found.",
                templatePath);
        }

        var dataDirectory = Path.Combine(contentRootPath, "data");
        var destinationPath = Path.Combine(dataDirectory, FileName);
        Directory.CreateDirectory(dataDirectory);

        if (File.Exists(destinationPath))
        {
            return new PersistentConfigurationFile(destinationPath, false);
        }

        var temporaryPath = Path.Combine(
            dataDirectory,
            $".{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.Copy(templatePath, temporaryPath, overwrite: false);
            try
            {
                File.Move(temporaryPath, destinationPath, overwrite: false);
                return new PersistentConfigurationFile(destinationPath, true);
            }
            catch (IOException) when (File.Exists(destinationPath))
            {
                // Another process may have initialized the shared directory first.
                return new PersistentConfigurationFile(destinationPath, false);
            }
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}

public sealed record PersistentConfigurationFile(string Path, bool Created);
