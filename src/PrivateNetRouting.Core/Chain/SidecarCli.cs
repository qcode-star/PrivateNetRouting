using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using PrivateNetRouting.Core.Configuration;

namespace PrivateNetRouting.Core.Chain;

/// <param name="AuthUrl">The tailnet authentication URL parsed out of the login output; null — none.</param>
public sealed record CliResult(int ExitCode, string Output, string? AuthUrl = null)
{
    public bool Ok => ExitCode == 0;
}

public interface ISidecarCli
{
    Task<CliResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct);

    /// <summary>Node BackendState from status --json; null — the CLI did not respond.</summary>
    Task<string?> GetBackendStateAsync(CancellationToken ct);

    /// <summary>
    /// The control server the node itself is bound to ("ControlURL" of `debug prefs` over our
    /// socket); null — the daemon did not answer or the prefs hold none yet (a fresh statedir).
    /// </summary>
    Task<string?> GetControlUrlAsync(CancellationToken ct);

    /// <summary>
    /// `tailscale up` with the fixed flag set; <paramref name="loginServer"/> — the control server
    /// the node uses (null — the default one). tailscale rejects an up that leaves out a
    /// non-default control server the node is bound to, so the caller passes the real one.
    /// </summary>
    Task<CliResult> UpAsync(string? loginServer, CancellationToken ct);

    Task<CliResult> DownAsync(CancellationToken ct);

    /// <summary>
    /// Interactive node login: runs `tailscale login`, hands the authentication URL to
    /// <paramref name="onAuthUrl"/> as soon as it appears in the output, and returns when the
    /// user finishes the authentication in the browser (or the loginWaitSeconds limit fires).
    /// </summary>
    Task<CliResult> LoginAsync(string? loginServer, Action<string>? onAuthUrl, CancellationToken ct);
}

/// <summary>
/// Wrapper around `tailscale --socket &lt;pipe&gt; …` (a direct launch).
/// </summary>
public sealed partial class SidecarCli(ChainTailscaleConfig config) : ISidecarCli
{
    [GeneratedRegex(@"""BackendState"":\s*""([^""]+)""")]
    private static partial Regex BackendStateRegex();

    /// <summary>On top of upTimeoutSeconds: the CLI's own --timeout must fire first, the wrapper limit is a safety net.</summary>
    private const int UpTimeoutMarginSeconds = 10;

    public async Task<CliResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var command = arguments.Count > 0 ? arguments[0] : "cli";
        // a daemon that stopped answering its pipe leaves the CLI hanging forever — a hard limit
        // turns that into an error (and kills the process) instead of a hung raise/stop/poll
        var timeout = TimeSpan.FromSeconds(
            command == "up" ? config.UpTimeoutSeconds + UpTimeoutMarginSeconds : config.CliTimeoutSeconds);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        var info = new ProcessStartInfo(config.CliPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add("--socket");
        info.ArgumentList.Add(config.Socket);
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)
            ?? throw new IOException($"failed to start {config.CliPath}");
        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
            await process.WaitForExitAsync(timeoutCts.Token);
            var output = (await stdoutTask + await stderrTask).Trim();
            return new CliResult(process.ExitCode, output);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // our own limit fired, not the caller's cancellation
            throw new IOException(
                $"tailscale {command} did not exit within {(int)timeout.TotalSeconds} s — the daemon pipe is unresponsive");
        }
        finally
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // lost the race with the process exiting on its own — nothing to kill
                }
            }
        }
    }

    public async Task<string?> GetBackendStateAsync(CancellationToken ct)
    {
        try
        {
            var result = await RunAsync(["status", "--json"], ct);
            var match = BackendStateRegex().Match(result.Output);
            return match.Success ? match.Groups[1].Value : null;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    public async Task<string?> GetControlUrlAsync(CancellationToken ct)
    {
        try
        {
            var result = await RunAsync(["debug", "prefs"], ct);
            return result.Ok ? SystemControlUrlFinder.ParseControlUrl(result.Output) : null;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    public async Task<CliResult> UpAsync(string? loginServer, CancellationToken ct)
    {
        // set-like flag set only: up overwrites the unspecified with defaults
        var arguments = new List<string>
        {
            "up",
            "--accept-dns=false",
            "--accept-routes",
            $"--timeout={config.UpTimeoutSeconds}s",
        };
        if (!string.IsNullOrWhiteSpace(loginServer))
        {
            arguments.Add($"--login-server={loginServer}");
        }

        return await RunAsync(arguments, ct);
    }

    public Task<CliResult> DownAsync(CancellationToken ct) => RunAsync(["down"], ct);

    public async Task<CliResult> LoginAsync(string? loginServer, Action<string>? onAuthUrl, CancellationToken ct)
    {
        // the wait covers the browser roundtrip (open the page, SSO, device approval); the
        // caller's cancellation (the GUI Stop, Ctrl+C) aborts it at any moment. The CLI has no
        // --timeout of its own here — the wrapper limit is the real one.
        var timeout = TimeSpan.FromSeconds(config.LoginWaitSeconds);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        var info = new ProcessStartInfo(config.CliPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add("--socket");
        info.ArgumentList.Add(config.Socket);
        info.ArgumentList.Add("login");
        if (!string.IsNullOrWhiteSpace(loginServer))
        {
            info.ArgumentList.Add($"--login-server={loginServer}");
        }

        using var process = Process.Start(info)
            ?? throw new IOException($"failed to start {config.CliPath}");
        var output = new StringBuilder();
        var extractor = new AuthUrlExtractor();
        string? authUrl = null;
        var gate = new object();
        try
        {
            async Task Pump(StreamReader reader)
            {
                while (await reader.ReadLineAsync(timeoutCts.Token) is { } line)
                {
                    string? announce = null;
                    lock (gate)
                    {
                        // both pumps share the output, the extractor and the url: one at a time
                        output.AppendLine(line);
                        if (authUrl is null && extractor.Feed(line) is { } found)
                        {
                            authUrl = found;
                            announce = found;
                        }
                    }

                    if (announce is not null)
                    {
                        onAuthUrl?.Invoke(announce); // outside the lock: the callback may take its time
                    }
                }
            }

            var pumps = Task.WhenAll(Pump(process.StandardOutput), Pump(process.StandardError));
            await process.WaitForExitAsync(timeoutCts.Token);
            await pumps;
            lock (gate)
            {
                return new CliResult(process.ExitCode, output.ToString().Trim(), authUrl);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // our own limit fired, not the caller's cancellation
            throw new IOException(
                $"tailscale login did not complete within {(int)timeout.TotalSeconds} s — the authentication was not finished in the browser (or the daemon pipe is unresponsive)");
        }
        finally
        {
            if (!process.HasExited)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // lost the race with the process exiting on its own — nothing to kill
                }
            }
        }
    }
}

/// <summary>
/// Pulls the tailnet authentication URL out of the tailscale login output: the URL follows the
/// "To authenticate, visit" hint (usually on the next line); any URL before the hint (an echoed
/// control server and the like) is ignored. Reports once.
/// </summary>
internal sealed partial class AuthUrlExtractor
{
    private bool _hintSeen;

    public string? Feed(string line)
    {
        if (line.Contains("To authenticate", StringComparison.OrdinalIgnoreCase))
        {
            _hintSeen = true;
        }

        if (!_hintSeen)
        {
            return null;
        }

        var match = UrlRegex().Match(line);
        if (!match.Success)
        {
            return null;
        }

        _hintSeen = false;
        return match.Value;
    }

    [GeneratedRegex(@"https?://\S+")]
    private static partial Regex UrlRegex();
}
