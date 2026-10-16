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

        // WSL_UTF8=1 switches the management output (`--list`) from UTF-16LE to UTF-8; the callers
        // decode as UTF-16 (the script output of a distro is not affected), so the switch is dropped
        info.Environment.Remove("WSL_UTF8");

        using var process = Process.Start(info)
            ?? throw new IOException("failed to start wsl.exe");

        // a cancelled call (the stopper's own timeout included) must not leave a hung wsl.exe behind
        using var killOnCancel = ct.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // already gone
            }
        });
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
    IWslProcessRunner wsl,
    TimeSpan? distroTimeout = null) : IWslAppHost
{
    /// <summary>
    /// Worst case of the stop script (RPC stops, SIGTERM and SIGKILL waits of both passes) with a
    /// margin; a wedged wsl.exe or app host must not hold the idle stop — and with it the chain
    /// stop that follows — forever.
    /// </summary>
    private static readonly TimeSpan DefaultDistroTimeout = TimeSpan.FromSeconds(120);

    private static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(30);

    public async Task<WslHostStopResult> StopAsync(CancellationToken ct)
    {
        string listing;
        using (var listCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            listCts.CancelAfter(ListTimeout);
            try
            {
                listing = await wsl.RunAsync(["--list", "--running", "--quiet"], null, Encoding.Unicode, listCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new IOException($"wsl.exe --list did not answer within {ListTimeout.TotalSeconds:0} s");
            }
        }

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
            var limit = distroTimeout ?? DefaultDistroTimeout;
            using var distroCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            distroCts.CancelAfter(limit);
            WslHostStopResult result;
            try
            {
                var output = await wsl.RunAsync(["-d", distro, "-u", "root", "--", "bash", "-s"], Script(stopClaude, stopCodex), Encoding.UTF8, distroCts.Token);
                result = Parse(output);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // the caller is not cancelling: this distro simply did not finish — report it as
                // unconfirmed and go on to the next distro and, in the end, the chain stop
                result = new WslHostStopResult(WslHostState.Stuck, $"timed out after {limit.TotalSeconds:0} s");
            }

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
        # This script and its ancestors are never targets: a CODEX_HOME forwarded through WSLENV
        # (or exported by the user's profile) would otherwise make the stopper kill itself.
        # /proc/<pid>/environ is the environment a process was exec'd with, so every subshell the
        # script forks shows the inherited CODEX_HOME too: the variable is dropped here (helpers
        # exec'd from now on do not carry it), and the scan runs in this very shell - builtins
        # only, no command substitution or pipeline around it - so no forked copy of the script
        # exists while /proc is being read.
        unset CODEX_HOME
        self_pids=" "
        q=$$
        while [ -n "$q" ] && [ "$q" -gt 0 ] 2>/dev/null; do
            self_pids="$self_pids$q "
            q=$(sed -e 's/^.*) //' "/proc/$q/stat" 2>/dev/null | awk '{print $2}')
        done
        CODEX_PIDS=""
        CODEX_N=0
        codex_scan() {
            local p pid entry
            CODEX_PIDS=""
            CODEX_N=0
            for p in /proc/[0-9]*; do
                pid=${p#/proc/}
                case "$self_pids" in *" $pid "*) continue ;; esac
                [ -r "$p/environ" ] || continue
                while IFS= read -r -d '' entry; do
                    case "$entry" in
                        CODEX_HOME=*)
                            CODEX_PIDS="$CODEX_PIDS $pid"
                            CODEX_N=$((CODEX_N + 1))
                            break
                            ;;
                    esac
                done 2>/dev/null < "$p/environ"
            done
        }
        codex_wait_empty() {
            local i=0
            while [ "$i" -lt "$1" ]; do
                codex_scan
                [ "$CODEX_N" -eq 0 ] && return 0
                sleep 1
                i=$((i + 1))
            done
            return 1
        }

        was_any=0
        codex_left=0
        if [ "$STOP_CODEX" = "1" ]; then
            codex_scan
            codex_left=$CODEX_N
            if [ "$codex_left" -gt 0 ]; then
                was_any=1
                echo "CODEX=$codex_left (CODEX_HOME env) - SIGTERM"
                kill -TERM $CODEX_PIDS 2>/dev/null
                if ! codex_wait_empty 10; then
                    echo "ESCALATE: SIGTERM did not help, codex left $CODEX_N - SIGKILL"
                    kill -KILL $CODEX_PIDS 2>/dev/null
                    sleep 2
                fi
                codex_scan
                codex_left=$CODEX_N
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
                    out=$(timeout 20 "$exe" --stop --socket "$sock" 2>&1 | head -n 1)
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
