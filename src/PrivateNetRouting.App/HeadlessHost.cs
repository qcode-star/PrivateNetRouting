using System.IO;
using PrivateNetRouting.Core.Chain;
using PrivateNetRouting.Core.Guard;
using PrivateNetRouting.Core.Net.Proxy;
using PrivateNetRouting.Core.SystemOps;
using PrivateNetRouting.Core.Watchdog;

namespace PrivateNetRouting.App;

/// <summary>
/// The windowless roles of the GUI exe: the detached proxy worker (--proxy-worker, spawned by the
/// supervisor), the detached idle stopper (--watchdog-worker) and the elevated helper
/// (--elevated-*, started through runas). They run from Main on the pool, without WPF: no
/// Application, no dispatcher, no single-instance mutex — a failure inside a worker cannot park
/// or deadlock a UI thread, and there is no hidden window that has to answer the session end.
/// </summary>
internal static class HeadlessHost
{
    /// <summary>True when the arguments name one of the roles; <paramref name="exitCode"/> is then the process exit code.</summary>
    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Contains(ForwardProxySupervisor.WorkerMarker, StringComparer.OrdinalIgnoreCase))
        {
            exitCode = Run(() => ProxyWorker.RunAsync(CancellationToken.None, args));
            return true;
        }

        if (args.Contains(WatchdogSupervisor.WorkerMarker, StringComparer.OrdinalIgnoreCase))
        {
            exitCode = Run(() => WatchdogWorker.RunAsync(CancellationToken.None));
            return true;
        }

        return TryRunElevatedHelper(args, out exitCode);
    }

    /// <summary>
    /// On the pool, with no synchronization context: the role's awaits (the watchdog loop, the
    /// runtime disposal, PowerShell and the sidecar CLI of the helper commands) continue on pool
    /// threads, and the main thread only waits for the result.
    /// </summary>
    private static int Run(Func<Task<int>> role)
    {
        InstallCrashLogging();
        return Task.Run(role).GetAwaiter().GetResult();
    }

    private static bool TryRunElevatedHelper(string[] args, out int exitCode)
    {
        exitCode = 0;
        var command = args.FirstOrDefault() ?? "";
        if (!ElevatedHelperArguments.IsHelperCommand(command))
        {
            return false;
        }

        // a helper command is never the GUI: without a result file there is nobody to report to,
        // and starting the window as an administrator is the wrong answer
        var resultFile = ElevatedHelperArguments.ParseOption(args, ElevatedHelperArguments.ResultFileOption);
        if (resultFile is null)
        {
            exitCode = 2;
            return true;
        }

        // the config of the user who asked: under over-the-shoulder UAC this process runs as another account
        var configPath = ElevatedHelperArguments.ParseOption(args, ElevatedHelperArguments.ConfigOption);
        var profileEnvironment = ElevatedHelperArguments.ParseOptions(args, ElevatedHelperArguments.EnvOption);
        exitCode = Run(() => Task.FromResult(GuardElevatedCommands.IsCommand(command)
            ? GuardElevatedCommands.Execute(command, resultFile, configPath, profileEnvironment)
            : ElevatedChainCommands.Execute(command, resultFile, configPath, profileEnvironment)));
        return true;
    }

    private static void InstallCrashLogging()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogCrash("domain", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogCrash("task", args.Exception);
            args.SetObserved();
        };
    }

    /// <summary>The fallback crash log in the default log folder; never throws.</summary>
    public static void LogCrash(string source, Exception? ex)
    {
        try
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PrivateNetRouting", "logs");
            Directory.CreateDirectory(folder);
            File.AppendAllText(
                Path.Combine(folder, "app-crash.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} unhandled exception ({source}): {ex}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // the safety net itself must not fail the process
        }
    }
}
