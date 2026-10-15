using System.Diagnostics;
using System.Text;

namespace PrivateNetRouting.Core.Watchdog;

public enum WslHostState
{
    /// <summary>There is nothing of ours in the distros (or no configured distro is running).</summary>
    None,

    /// <summary>The hosts are stopped (by RPC or by signal escalation).</summary>
    Stopped,

    /// <summary>Some processes remain — the stop was not confirmed.</summary>
    Stuck,
}

public sealed record WslHostStopResult(WslHostState State, string Detail);

/// <summary>
/// Graceful stop of the app hosts the Windows desktop apps leave inside the WSL distros:
/// the Claude remote host (daemon remote/srv/&lt;hash&gt;/server is stopped by its own binary via
/// RPC server.shutdown, ccd-cli sessions by the same; orphans — SIGTERM → SIGKILL escalation)
/// and the leftover Codex processes (SIGTERM → SIGKILL; recognized by CODEX_HOME in their
/// environment — the desktop app's signature, a manually started codex CLI stays alone).
/// Only the RUNNING distros of the configured list are touched — no distro is booted just for
/// a check; the rest of the distro is left alone.
/// </summary>
public interface IWslAppHost
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
            await process.StandardInput.WriteAsync(NormalizeScript(standardInput).AsMemory(), ct);
            process.StandardInput.Close();
        }

        await process.WaitForExitAsync(ct);
        return await stdoutTask + await stderrTask;
    }

    /// <summary>
    /// bash scripts are raw string literals in the source and capture its line endings: a CRLF
    /// checkout turns every line into "…\r", bash dies on the first one ("then\r" is a syntax
    /// error) and wsl.exe exit codes do not surface it. Every script fed to a distro goes
    /// through here, so LF is enforced.
    /// </summary>
    internal static string NormalizeScript(string script) =>
        script.Replace("\r\n", "\n").Replace("\r", "\n");
}

/// <summary>
/// Stopper via `wsl.exe -d &lt;distro&gt; -u root -- bash -s`: first the running distros are listed
/// (`--list --running`, wsl.exe output is UTF-16LE with possible NULs) and intersected with the
/// configured names — a distro that is not running is skipped without being started. In every
/// running configured distro the stop script runs once (the Claude and Codex passes are gated
/// by the stopClaude/stopCodex flags — the apps disabled in the config are not touched).
/// </summary>
public sealed class WslAppHostStopper(
    IReadOnlyList<string> distros,
    bool stopClaude,
    bool stopCodex,
    IWslProcessRunner wsl) : IWslAppHost
{
    public async Task<WslHostStopResult> StopAsync(CancellationToken ct)
    {
        var listing = await wsl.RunAsync(["--list", "--running", "--quiet"], null, Encoding.Unicode, ct);
        var running = listing.Split('\n')
            .Select(line => line.Replace("\0", "").Trim())
            .Where(line => line.Length > 0)
            .ToList();
        var targets = distros
            .Where(distro => !string.IsNullOrWhiteSpace(distro))
            .Where(distro => running.Contains(distro.Trim(), StringComparer.OrdinalIgnoreCase))
            .Select(distro => distro.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (targets.Count == 0)
        {
            return new WslHostStopResult(WslHostState.None, "SKIP=no configured distro is running");
        }

        var details = new List<string>();
        var state = WslHostState.None;
        foreach (var distro in targets)
        {
            // as root, like the guard: another default user cannot read /proc/*/environ or the exe
            // links of the app hosts, nor signal them, and the stop would silently find nothing
            var output = await wsl.RunAsync(["-d", distro, "-u", "root", "--", "bash", "-s"], Script(stopClaude, stopCodex), Encoding.UTF8, ct);
            var result = Parse(output);
            details.Add($"[{distro}] {result.State}: {result.Detail}");
            if (result.State == WslHostState.Stuck || (result.State == WslHostState.Stopped && state == WslHostState.None))
            {
                state = result.State;
            }
        }

        return new WslHostStopResult(state, string.Join(" | ", details));
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

    /// <summary>
    /// The stop script of one distro. Output contract: detail lines (English) plus the final
    /// STATE= line (none | stopped | stuck). The Claude pass: the daemon is stopped by its own
    /// binary (RPC), sessions and orphans — SIGTERM → SIGKILL. The Codex pass: every process
    /// whose environment carries CODEX_HOME (what the desktop app passes to its WSL processes —
    /// the same signature the profile.d snippet keys on), SIGTERM → SIGKILL.
    /// </summary>
    internal static string Script(bool stopClaude, bool stopCodex) => $$"""
        set -u

        STOP_CLAUDE={{(stopClaude ? 1 : 0)}}
        STOP_CODEX={{(stopCodex ? 1 : 0)}}

        # --- codex: leftover processes of the desktop app (env carries CODEX_HOME) ---
        codex_pids() {
            for p in /proc/[0-9]*; do
                if tr '\0' '\n' < "$p/environ" 2>/dev/null | grep -q '^CODEX_HOME='; then
                    echo "${p#/proc/}"
                fi
            done
        }
        codex_count() { codex_pids | wc -l | tr -d ' '; }
        codex_wait_empty() {
            local i=0 left
            while [ "$i" -lt "$1" ]; do
                left=$(codex_count)
                [ "$left" = "0" ] && return 0
                sleep 1
                i=$((i + 1))
            done
            return 1
        }

        was_any=0
        codex_left=0
        if [ "$STOP_CODEX" = "1" ]; then
            codex_left=$(codex_count)
            if [ "$codex_left" -gt 0 ]; then
                was_any=1
                echo "CODEX=$codex_left (CODEX_HOME env) - SIGTERM"
                kill -TERM $(codex_pids) 2>/dev/null
                if ! codex_wait_empty 10; then
                    echo "ESCALATE: SIGTERM did not help, codex left $(codex_count) - SIGKILL"
                    kill -KILL $(codex_pids) 2>/dev/null
                    sleep 2
                fi
                codex_left=$(codex_count)
                echo "CODEX_REMAIN=$codex_left"
            fi
        fi

        # --- claude: the remote host daemon + its ccd-cli sessions ---
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

        if [ "$STOP_CLAUDE" = "1" ]; then
            daemons=$(list_daemons)
            children=$(list_children)
            if [ -n "$daemons" ]; then
                was_any=1
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
            elif [ -n "$children" ]; then
                # no daemon, but ccd-cli sessions are left (daemon was killed by hand) - they still
                # hold the distro and memory, stop them.
                was_any=1
                echo "ORPHANS=$(echo "$children" | wc -l | tr -d ' ') (no daemon, ccd-cli sessions left) - SIGTERM"
                kill -TERM $children 2>/dev/null
                if ! wait_empty 10; then
                    echo "ESCALATE: SIGTERM did not help, left $(count_all) - SIGKILL"
                    kill -KILL $(list_children) 2>/dev/null
                    sleep 2
                fi
            fi
        fi

        claude_left=$(count_all)
        total=$((claude_left + codex_left))
        echo "REMAIN=$total"
        if [ "$total" = "0" ]; then
            [ "$was_any" = "1" ] && echo "STATE=stopped" || echo "STATE=none"
        else
            echo "STATE=stuck"
        fi
        """;
}
