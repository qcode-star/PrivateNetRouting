using System.Diagnostics;
using System.Text;

namespace PrivateNetRouting.Core.Watchdog;

public enum WslHostState
{
    /// <summary>There is neither a daemon nor sessions in the distro (or the distro is not running).</summary>
    None,

    /// <summary>The host is stopped (by RPC or by signal escalation).</summary>
    Stopped,

    /// <summary>Some processes remain — the stop was not confirmed.</summary>
    Stuck,
}

public sealed record WslHostStopResult(WslHostState State, string Detail);

/// <summary>
/// Graceful stop of the WSL host of Claude Desktop: the daemon
/// remote/srv/&lt;hash&gt;/server is stopped by its own binary (RPC server.shutdown),
/// ccd-cli sessions are shut down by the same; for orphaned processes — SIGTERM → SIGKILL escalation.
/// The rest of the distro and a manually started Claude Code CLI are left alone; the distro
/// is not started just for a check.
/// </summary>
public interface IWslClaudeHost
{
    Task<WslHostStopResult> StopAsync(CancellationToken ct);
}

/// <summary>Running wsl.exe with arguments and optional stdin (a fake for tests).</summary>
public interface IWslProcessRunner
{
    Task<string> RunAsync(IReadOnlyList<string> arguments, string? standardInput, Encoding outputEncoding, CancellationToken ct);
}

public sealed class WslProcessRunner : IWslProcessRunner
{
    public async Task<string> RunAsync(IReadOnlyList<string> arguments, string? standardInput, Encoding outputEncoding, CancellationToken ct)
    {
        var info = new ProcessStartInfo("wsl.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null,
            StandardOutputEncoding = outputEncoding,
            StandardErrorEncoding = outputEncoding,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)
            ?? throw new IOException("failed to start wsl.exe");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput.AsMemory(), ct);
            process.StandardInput.Close();
        }

        await process.WaitForExitAsync(ct);
        return await stdoutTask + await stderrTask;
    }
}

/// <summary>
/// Stopper via `wsl.exe -d &lt;distro&gt; -- bash -s`: the bash script (only the message text is English
/// and the remaining processes are counted after escalation). It first checks that the distro is running (`--list --running`,
/// wsl.exe output is UTF-16LE with possible NULs), otherwise the step is skipped without starting the distro.
/// </summary>
public sealed class WslClaudeHostStopper(string distro, IWslProcessRunner wsl) : IWslClaudeHost
{
    public async Task<WslHostStopResult> StopAsync(CancellationToken ct)
    {
        var listing = await wsl.RunAsync(["--list", "--running", "--quiet"], null, Encoding.Unicode, ct);
        var running = listing.Split('\n')
            .Select(line => line.Replace("\0", "").Trim())
            .Where(line => line.Length > 0)
            .ToList();
        if (!running.Contains(distro, StringComparer.OrdinalIgnoreCase))
        {
            return new WslHostStopResult(WslHostState.None, $"SKIP=distro-not-running ({distro})");
        }

        var output = await wsl.RunAsync(["-d", distro, "--", "bash", "-s"], StopScript, Encoding.UTF8, ct);
        return Parse(output);
    }

    internal static WslHostStopResult Parse(string output)
    {
        var lines = output.Split('\n').Select(line => line.Trim()).ToList();
        var stateLine = lines.FirstOrDefault(line => line.StartsWith("STATE=", StringComparison.Ordinal));
        var detail = string.Join(" | ", lines.Where(line => line.Length > 0 && !line.StartsWith("STATE=", StringComparison.Ordinal)));
        var state = stateLine?["STATE=".Length..] switch
        {
            "none" => WslHostState.None,
            "stopped" => WslHostState.Stopped,
            "stuck" => WslHostState.Stuck,
            _ => WslHostState.Stuck, // bash did not answer with a state — treat as an unconfirmed stop
        };
        if (stateLine is null)
        {
            detail = detail.Length > 0 ? $"no STATE in output: {detail}" : "no STATE in output";
        }

        return new WslHostStopResult(state, detail);
    }

    internal const string StopScript = """
        set -u

        list_daemons() { ps -eo pid,args --no-headers 2>/dev/null | awk '$2 ~ /\/\.claude\/remote\/srv\/[^/]+\/server$/ { print $1 }'; }
        list_children() { ps -eo pid,args --no-headers 2>/dev/null | awk '$2 ~ /\/\.claude\/remote\/ccd-cli\// { print $1 }'; }
        count_all() { ps -eo pid,args --no-headers 2>/dev/null | awk '$2 ~ /\/\.claude\/remote\/(srv|ccd-cli)\// { print $1 }' | wc -l | tr -d ' '; }
        wait_empty() {
            local i=0 left
            while [ "$i" -lt "$1" ]; do
                left=$(count_all)
                [ "$left" = "0" ] && return 0
                sleep 1
                i=$((i + 1))
            done
            return 1
        }

        daemons=$(list_daemons)
        if [ -z "$daemons" ]; then
            children=$(list_children)
            if [ -z "$children" ]; then
                echo "STATE=none"
                exit 0
            fi
            # no daemon, but ccd-cli sessions are left (daemon was killed by hand) - they still
            # hold the distro and memory, stop them.
            echo "ORPHANS=$(echo "$children" | wc -l | tr -d ' ') (no daemon, ccd-cli sessions left) - SIGTERM"
            kill -TERM $children 2>/dev/null
            if ! wait_empty 10; then
                echo "ESCALATE: SIGTERM did not help, left $(count_all) - SIGKILL"
                kill -KILL $(list_children) 2>/dev/null
                sleep 2
            fi
            left=$(count_all)
            echo "REMAIN=$left"
            [ "$left" = "0" ] && echo "STATE=stopped" || echo "STATE=stuck"
            exit 0
        fi

        for p in $daemons; do
            exe=$(readlink -f "/proc/$p/exe" 2>/dev/null || echo "")
            sock=$(tr '\0' '\n' < "/proc/$p/cmdline" 2>/dev/null | awk '/^--socket$/{getline; print; exit}')
            if [ -z "$exe" ] || [ -z "$sock" ] || [ ! -S "$sock" ]; then
                echo "SKIP pid=$p (exe='$exe' socket='$sock')"
                continue
            fi
            out=$("$exe" --stop --socket "$sock" 2>&1 | head -n 1)
            echo "STOP pid=$p -> ${out:-<no output>}"
        done

        if ! wait_empty 15; then
            echo "ESCALATE: after the RPC stop left $(count_all) - SIGTERM (daemon + sessions)"
            kill -TERM $(list_daemons) $(list_children) 2>/dev/null
            if ! wait_empty 10; then
                echo "ESCALATE: SIGTERM did not help, left $(count_all) - SIGKILL"
                kill -KILL $(list_daemons) $(list_children) 2>/dev/null
                sleep 2
            fi
        fi

        left=$(count_all)
        echo "REMAIN=$left"
        [ "$left" = "0" ] && echo "STATE=stopped" || echo "STATE=stuck"
        """;
}
