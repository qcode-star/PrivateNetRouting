namespace PrivateNetRouting.App;

/// <summary>
/// The process entry point. The background roles of this exe — the detached proxy worker, the
/// detached idle stopper and the elevated helper — have no window and need no dispatcher: they
/// run here, on the pool, before WPF is touched (see HeadlessHost). Everything else is the GUI.
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (HeadlessHost.TryRun(args, out var exitCode))
        {
            return exitCode;
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
