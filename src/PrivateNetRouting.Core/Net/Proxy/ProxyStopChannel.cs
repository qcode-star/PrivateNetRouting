namespace PrivateNetRouting.Core.Net.Proxy;

/// <summary>
/// How a process that does not own the proxy listener asks the process that does (the GUI in
/// the tray, a foreground `pnr proxy`) to stop it: the detached idle stopper cannot kill the GUI
/// that hosts the in-process proxy, but the idle stop must take the whole chain down.
/// </summary>
public interface IProxyStopChannel
{
    /// <summary>The owner side: <paramref name="onStopRequested"/> runs when someone calls <see cref="Request"/> for this port. Dispose ends the listening.</summary>
    IDisposable Listen(int port, Func<Task> onStopRequested);

    /// <summary>The asking side: true when an owner is listening on the port and was signalled.</summary>
    bool Request(int port);
}

/// <summary>
/// A named event per port in the session-local namespace (the GUI and the stopper are the same
/// user in the same session): the owner waits on it, the asker opens it and sets it. Anyone able to
/// set it could kill the proxy process anyway, so it carries no extra authority.
/// </summary>
public sealed class NamedEventProxyStopChannel : IProxyStopChannel
{
    private static string EventName(int port) => $@"Local\PrivateNetRouting.ProxyStop.{port}";

    public IDisposable Listen(int port, Func<Task> onStopRequested) => new Listener(EventName(port), onStopRequested);

    public bool Request(int port)
    {
        if (!EventWaitHandle.TryOpenExisting(EventName(port), out var handle))
        {
            return false;
        }

        using (handle)
        {
            return handle.Set();
        }
    }

    private sealed class Listener : IDisposable
    {
        private readonly EventWaitHandle _event;
        private readonly RegisteredWaitHandle _registration;

        public Listener(string name, Func<Task> onStopRequested)
        {
            _event = new EventWaitHandle(false, EventResetMode.AutoReset, name);
            _registration = ThreadPool.RegisterWaitForSingleObject(
                _event,
                (_, _) => _ = Task.Run(onStopRequested),
                null,
                Timeout.Infinite,
                executeOnlyOnce: false);
        }

        public void Dispose()
        {
            _registration.Unregister(null);
            _event.Dispose();
        }
    }
}
