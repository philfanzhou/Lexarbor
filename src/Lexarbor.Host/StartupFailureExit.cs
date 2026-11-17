namespace Lexarbor.Host;

/// <summary>
/// Ends the process when a startup failure is thrown before the host runs — a
/// missing or invalid connection string, a refused database startup gate.
/// </summary>
/// <remarks>
/// In a container the process is PID 1, which the kernel does not terminate
/// with the runtime's default abort, so an unhandled exception would leave the
/// container running without serving. Every such startup diagnostic is a fixed
/// message that carries no connection string, path, or SQL; any other
/// exception is reported by type only. The handler is registered once per
/// process, however many hosts the process builds.
/// </remarks>
internal static class StartupFailureExit
{
    private static int _registered;

    internal static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1)
        {
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            Console.Error.WriteLine(eventArgs.ExceptionObject is InvalidOperationException exception
                ? $"Lexarbor startup failed: {exception.Message}"
                : $"Lexarbor stopped after an unhandled {eventArgs.ExceptionObject.GetType().FullName}.");
            Environment.Exit(1);
        };
    }
}
