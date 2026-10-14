using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using PrivateNetRouting.Core.Chain;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Guard;
using PrivateNetRouting.Core.Logging;
using PrivateNetRouting.Core.SystemOps;
using PrivateNetRouting.Core.Watchdog;

namespace PrivateNetRouting.Tests;

// ----- guard system-part fakes -----

internal sealed class FakeEgressPolicyStore : IEgressPolicyStore
{
    public EgressPolicyValues Values { get; set; } = new(false, null, null);

    public string? AppliedUrl { get; private set; }

    public bool Removed { get; private set; }

    public EgressPolicyValues Read() => Values;

    public void Apply(string proxyUrl) => AppliedUrl = proxyUrl;

    public void Remove() => Removed = true;
}

internal sealed class FakeBanSystemOps : IBanSystemOps
{
    public BanLayersState Layers { get; set; } = BanLayersState.Empty;

    public List<string> Calls { get; } = [];

    public Task<BanLayersState> GetLayersStateAsync(CancellationToken ct)
    {
        Calls.Add("status");
        return Task.FromResult(Layers);
    }

    public Task ApplyNrptAsync(CancellationToken ct)
    {
        Calls.Add("nrpt-on");
        return Task.CompletedTask;
    }

    public Task RemoveNrptAsync(CancellationToken ct)
    {
        Calls.Add("nrpt-off");
        return Task.CompletedTask;
    }

    public Task ApplyFirewallAsync(CancellationToken ct)
    {
        Calls.Add("fw-on");
        return Task.CompletedTask;
    }

    public Task RemoveFirewallAsync(CancellationToken ct)
    {
        Calls.Add("fw-off");
        return Task.CompletedTask;
    }

    public Task FlushDnsCacheAsync(CancellationToken ct)
    {
        Calls.Add("flush");
        return Task.CompletedTask;
    }
}

internal sealed class FakeSystemProxyStore : ISystemProxyStore
{
    public SystemProxyStatus Status { get; set; } = new(false, null, null);

    public (string? Server, string? Bypass)? EnabledWith { get; private set; }

    public bool Disabled { get; private set; }

    public SystemProxyStatus Read() => Status;

    public void Enable(string server, string bypass) => EnabledWith = (server, bypass);

    public void Disable() => Disabled = true;
}

internal sealed class FakeElevatedRunner(bool ok = true, string detail = "done") : IElevatedCommandRunner
{
    public List<string> Commands { get; } = [];

    public Task<ElevatedResult> RunAsync(string command, CancellationToken ct)
    {
        Commands.Add(command);
        return Task.FromResult(new ElevatedResult(ok, detail));
    }
}

/// <summary>wsl.exe by arguments: distro lists and canned answers to bash -s.</summary>
internal sealed class FakeWslRunner : IWslProcessRunner
{
    public List<string> Running { get; set; } = [];

    public List<string> Installed { get; set; } = [];

    /// <summary>Answer of the root pass (state read) by distro name; the completion marker is appended.</summary>
    public Func<string, string> ProbeOutput { get; set; } = _ => "SCRIPT=missing\nSETTINGS=missing\n";

    /// <summary>Output returned for the writing scripts (settings/startup/codex env); without the marker the sync must fail.</summary>
    public string WriteOutput { get; set; } = "SETTINGS=written\nSCRIPT=written\nENVFILE=written\nPNR=ok\n";

    /// <summary>stdin of scripts passed to the distro: (distro, user, script).</summary>
    public List<(string Distro, string? User, string Script)> Scripts { get; } = [];

    public Task<string> RunAsync(IReadOnlyList<string> arguments, string? standardInput, Encoding outputEncoding, CancellationToken ct)
    {
        if (arguments.Contains("--list"))
        {
            var names = arguments.Contains("--running") ? Running : Installed;
            return Task.FromResult(string.Join("\r\n", names.Select(n => n + "\0")));
        }

        // "-d <distro> -u <user> -- bash -s"
        var distro = arguments[IndexOf(arguments, "-d") + 1];
        var user = arguments[IndexOf(arguments, "-u") + 1];
        if (standardInput is not null)
        {
            // the reading probe (… echo SETTINGS=END) answers with the canned output;
            // the writing scripts (settings/startup) are only recorded
            if (standardInput.Contains("echo SETTINGS=END"))
            {
                return Task.FromResult(ProbeOutput(distro) + "PNR=ok\n");
            }

            Scripts.Add((distro, user, standardInput));
            return Task.FromResult(WriteOutput);
        }

        return Task.FromResult(ProbeOutput(distro) + "PNR=ok\n");
    }

    private static int IndexOf(IReadOnlyList<string> arguments, string value)
    {
        for (var i = 0; i < arguments.Count; i++)
        {
            if (arguments[i] == value)
            {
                return i;
            }
        }

        return -1;
    }
}

public class GuardTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("pnr-guard-").FullName;

    private readonly List<FileLog> _logs = [];

    // ----- hosts block -----

    [Fact]
    public void HostsBlockAppliesAndReads()
    {
        var path = Path.Combine(_tempDir, "hosts");
        File.WriteAllLines(path, ["127.0.0.1 localhost", "10.0.0.1 corp.internal", "", ""]);
        var domains = new List<string> { "claude.ai", "openai.com" };

        HostsFileBlock.Apply(path, "# >>> ban", "# <<< ban", "# comment", domains);

        var state = HostsFileBlock.Read(path, "# >>> ban", "# <<< ban", "# comment", domains);
        Assert.True(state.Present);
        Assert.Equal(2, state.EntryCount);
        Assert.True(state.Current);

        var text = File.ReadAllText(path);
        Assert.Contains("10.0.0.1 corp.internal", text); // foreign lines untouched
        Assert.Contains("\r\n\r\n# >>> ban\r\n# comment\r\n0.0.0.0 claude.ai\r\n0.0.0.0 openai.com\r\n# <<< ban\r\n", text); // trailing blank lines are trimmed, the block is at the end
    }

    [Fact]
    public void HostsBlockReapplyIsNoop()
    {
        var path = Path.Combine(_tempDir, "hosts");
        File.WriteAllText(path, "127.0.0.1 localhost\r\n");
        var domains = new List<string> { "claude.ai" };
        HostsFileBlock.Apply(path, "# >>> b", "# <<< b", "# c", domains);

        // reapplying an already current block does not touch the file (readonly does not interfere)
        var attr = File.GetAttributes(path);
        File.SetAttributes(path, attr | FileAttributes.ReadOnly);
        try
        {
            HostsFileBlock.Apply(path, "# >>> b", "# <<< b", "# c", domains);
        }
        finally
        {
            File.SetAttributes(path, attr);
        }

        Assert.Equal("127.0.0.1 localhost\r\n\r\n# >>> b\r\n# c\r\n0.0.0.0 claude.ai\r\n# <<< b\r\n\r\n", File.ReadAllText(path));

        HostsFileBlock.Remove(path, "# >>> b", "# <<< b");

        // the block is gone, so removal again is a no-op too (readonly does not interfere)
        File.SetAttributes(path, attr | FileAttributes.ReadOnly);
        try
        {
            HostsFileBlock.Remove(path, "# >>> b", "# <<< b");
        }
        finally
        {
            File.SetAttributes(path, attr);
        }

        Assert.Equal("127.0.0.1 localhost\r\n", File.ReadAllText(path));
    }

    [Fact]
    public void HostsBlockUpdatesListAndRemoves()
    {
        var path = Path.Combine(_tempDir, "hosts");
        File.WriteAllText(path, "127.0.0.1 localhost\r\n");
        HostsFileBlock.Apply(path, "# >>> b", "# <<< b", "# c", ["claude.ai", "old.example"]);

        // the list changed, so the block is rebuilt
        HostsFileBlock.Apply(path, "# >>> b", "# <<< b", "# c", ["claude.ai", "new.example"]);
        var state = HostsFileBlock.Read(path, "# >>> b", "# <<< b", "# c", ["claude.ai", "new.example"]);
        Assert.True(state.Current);
        Assert.DoesNotContain("old.example", File.ReadAllText(path));

        HostsFileBlock.Remove(path, "# >>> b", "# <<< b");
        Assert.Equal("127.0.0.1 localhost\r\n", File.ReadAllText(path));
        Assert.False(HostsFileBlock.Read(path, "# >>> b", "# <<< b", "# c", []).Present);
    }

    // ----- env values -----

    [Fact]
    public void NoProxyPartsBuildBothFormsPerZone()
    {
        var parts = ClaudeEnvValues.NoProxyParts(["corp.example.com", ""]);
        Assert.Equal(["localhost", "127.0.0.1", ".local", "corp.example.com", ".corp.example.com"], parts);
        Assert.Equal("localhost,127.0.0.1,.local,corp.example.com,.corp.example.com", ClaudeEnvValues.BuildNoProxy(["corp.example.com"]));
    }

    [Fact]
    public void ProxyUrls()
    {
        var config = new AppConfig();
        config.ForwardProxy.Listen = "127.0.0.1:18082"; // pinned: the test is about URL building, not the default
        Assert.Equal("http://127.0.0.1:18082", ClaudeEnvValues.WindowsProxyUrl(config));
        Assert.Equal(18082, ClaudeEnvValues.Port(config));
        Assert.Null(ClaudeEnvValues.WslProxyUrl(null, config));
        Assert.Equal("http://192.0.2.1:18082", ClaudeEnvValues.WslProxyUrl(IPAddress.Parse("192.0.2.1"), config));

        // the skipped socks5 shape: the guard files carry chain.socks5.upstream instead
        config.Chain.Mode = "socks5";
        config.ForwardProxy.Enabled = false;
        config.Chain.Socks5.Upstream = "192.0.2.10:7080";
        Assert.Equal("http://192.0.2.10:7080", ClaudeEnvValues.WindowsProxyUrl(config));
        Assert.Equal(7080, ClaudeEnvValues.Port(config));
        Assert.Equal("http://192.0.2.1:7080", ClaudeEnvValues.WslProxyUrl(IPAddress.Parse("192.0.2.1"), config));
    }

    [Fact]
    public void WslReachabilityWarningOnlyForLoopbackExternalEndpoint()
    {
        // the default shape: our proxy binds the WSL gateway itself — nothing to warn about
        var config = new AppConfig();
        Assert.Null(WslConfigGuard.ReachabilityWarning(config));

        // the skip flag alone (tailscale mode) changes nothing
        config.ForwardProxy.Enabled = false;
        Assert.Null(WslConfigGuard.ReachabilityWarning(config));

        // a skipped shape with a public/LAN upstream is reachable in principle — no warning
        config.Chain.Mode = "socks5";
        config.Chain.Socks5.Upstream = "192.0.2.10:7080";
        Assert.Null(WslConfigGuard.ReachabilityWarning(config));

        // loopback-only upstream: unreachable from the distros — the read-only warning fires
        config.Chain.Socks5.Upstream = "127.0.0.1:7080";
        var warning = WslConfigGuard.ReachabilityWarning(config);
        Assert.NotNull(warning);
        Assert.Contains("127.0.0.1:7080", warning);
        Assert.Contains("loopback", warning);
    }

    [Fact]
    public void SystemProxyBypassBuildsLocalRangesExtrasAndZones()
    {
        var bypass = SystemProxyBypass.Build(["corp.example.com"], ["*.ru"]);
        Assert.Contains("localhost", bypass);
        Assert.Contains("10.*", bypass);
        Assert.Contains("172.16.*", bypass);
        Assert.Contains("172.31.*", bypass);
        Assert.DoesNotContain("172.32.*", bypass);
        Assert.Contains("*.ru", bypass);
        Assert.Contains("corp.example.com;*.corp.example.com", bypass);
        Assert.StartsWith("localhost", bypass);
    }

    // ----- settings.json (Windows) -----

    private AppConfig SettingsConfig()
    {
        var config = new AppConfig();
        config.ForwardProxy.Listen = "127.0.0.1:18082"; // the assertions expect the documented port, not the moving default
        config.BypassDomains = ["corp.example.com"];
        config.Claude.SettingsPath = Path.Combine(_tempDir, "settings.json");
        return config;
    }

    [Fact]
    public void SettingsSyncPreservesKeysAndBackups()
    {
        var config = SettingsConfig();
        File.WriteAllText(config.Claude.SettingsPath, """
            {
              "other": 1,
              "env": { "HTTPS_PROXY": "http://old:1", "FOO": "bar" }
            }
            """);

        var guard = new ClaudeSettingsGuard(config);
        var result = guard.Sync();

        Assert.True(result.Written);
        Assert.NotNull(result.BackupPath);
        Assert.Contains(".pnr-bak-", result.BackupPath);
        Assert.True(File.Exists(result.BackupPath));
        var status = guard.Read();
        Assert.Equal(GuardState.On, status.State);

        var json = JsonNode.Parse(File.ReadAllText(config.Claude.SettingsPath))!;
        Assert.Equal(1, (int)json["other"]!);
        Assert.Equal("bar", (string?)json["env"]!["FOO"]);
        Assert.Equal("http://127.0.0.1:18082", (string?)json["env"]!["HTTPS_PROXY"]);
        Assert.Equal("http://127.0.0.1:18082", (string?)json["env"]!["HTTP_PROXY"]);
        Assert.Equal("localhost,127.0.0.1,.local,corp.example.com,.corp.example.com", (string?)json["env"]!["NO_PROXY"]);

        // the second sync finds the env already correct: no rewrite, no backup
        var again = guard.Sync();
        Assert.False(again.Written);
        Assert.Null(again.BackupPath);
    }

    /// <summary>The skipped socks5 shape: the synced env carries chain.socks5.upstream, not the local listener.</summary>
    [Fact]
    public void SettingsSyncPointsAtTheUpstreamWhenSkipped()
    {
        var config = SettingsConfig();
        config.Chain.Mode = "socks5";
        config.ForwardProxy.Enabled = false;
        config.Chain.Socks5.Upstream = "192.0.2.10:7080";
        config.Codex.EnvPath = Path.Combine(_tempDir, "codex.env");

        var guard = new ClaudeSettingsGuard(config);
        guard.Sync();
        var status = guard.Read();
        Assert.Equal(GuardState.On, status.State);
        Assert.Equal("http://192.0.2.10:7080", status.ExpectedProxy);

        var json = JsonNode.Parse(File.ReadAllText(config.Claude.SettingsPath))!;
        Assert.Equal("http://192.0.2.10:7080", (string?)json["env"]!["HTTPS_PROXY"]);

        CodexEnvFile.Write(config);
        var env = File.ReadAllText(config.Codex.EnvPath);
        Assert.Contains("HTTP_PROXY=http://192.0.2.10:7080", env);
        Assert.Contains("ALL_PROXY=http://192.0.2.10:7080", env);
    }

    [Fact]
    public void SettingsSyncPrunesBackupsAndLegacyNaming()
    {
        var config = SettingsConfig();
        File.WriteAllText(config.Claude.SettingsPath, """{ "env": { "HTTPS_PROXY": "http://old:1" } }""");
        var dir = Path.GetDirectoryName(config.Claude.SettingsPath)!;
        var name = Path.GetFileName(config.Claude.SettingsPath);
        for (var i = 1; i <= 5; i++)
        {
            var stale = Path.Combine(dir, $"{name}.pnr-bak-2026010{i}-000000");
            File.WriteAllText(stale, "old");
            if (i <= 3)
            {
                File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2)); // preserved longest ago
            }
        }

        var legacy = Path.Combine(dir, $"{name}.bak-20260101-000000");
        File.WriteAllText(legacy, "previous naming");

        var guard = new ClaudeSettingsGuard(config);
        var result = guard.Sync();

        Assert.True(result.Written);
        var backups = Directory.GetFiles(dir, $"{name}.pnr-bak-*").ToList();
        Assert.Equal(3, backups.Count); // the fresh one plus the two most recently preserved — no more
        Assert.Contains(result.BackupPath, backups);
        Assert.True(File.Exists(Path.Combine(dir, $"{name}.pnr-bak-20260104-000000"))); // kept by mtime, not by name
        Assert.True(File.Exists(Path.Combine(dir, $"{name}.pnr-bak-20260105-000000")));
        Assert.False(File.Exists(legacy));
    }

    [Fact]
    public void SettingsBackupDeduplicatesAndReusesSlots()
    {
        var path = Path.Combine(_tempDir, "settings.json");
        const string v1 = """{ "env": { "HTTPS_PROXY": "http://old:1" } }""";
        File.WriteAllText(path, v1);

        var first = ClaudeSettingsGuard.Backup(path);
        Assert.Equal($"{path}.pnr-bak-1", first); // numbered slots instead of timestamped names
        File.WriteAllText(path, "v2");
        Assert.Equal($"{path}.pnr-bak-2", ClaudeSettingsGuard.Backup(path));

        // a state already preserved is not copied again — the matching copy is touched
        File.WriteAllText(path, v1);
        File.SetLastWriteTimeUtc(first!, DateTime.UtcNow.AddDays(-2));
        Assert.Null(ClaudeSettingsGuard.Backup(path));
        Assert.Equal(2, Directory.GetFiles(_tempDir, "*.pnr-bak-*").Length); // nothing new created
        Assert.True(File.GetLastWriteTimeUtc(first!) > DateTime.UtcNow.AddMinutes(-1)); // touched

        // all three slots taken: the one preserved longest ago is reused
        File.WriteAllText(path, "v3");
        Assert.Equal($"{path}.pnr-bak-3", ClaudeSettingsGuard.Backup(path));
        File.SetLastWriteTimeUtc(first!, DateTime.UtcNow.AddDays(-2));
        File.WriteAllText(path, "v4");
        Assert.Equal(first, ClaudeSettingsGuard.Backup(path));
        Assert.Equal("v4", File.ReadAllText(first!));
    }

    [Fact]
    public void SettingsStatusDetectsMissingMismatchAndAbsent()
    {
        var config = SettingsConfig();
        var guard = new ClaudeSettingsGuard(config);

        Assert.True(guard.Read().FileMissing); // the file is missing
        guard.Sync();
        Assert.Equal(GuardState.On, guard.Read().State);

        File.WriteAllText(config.Claude.SettingsPath, """{ "env": { "HTTPS_PROXY": "http://other:2" } }""");
        var mismatch = guard.Read();
        Assert.Equal(GuardState.Partial, mismatch.State);
        Assert.Equal("http://other:2", mismatch.CurrentProxy);
    }

    // ----- config.toml of Codex -----

    private AppConfig CodexConfig()
    {
        var config = new AppConfig();
        config.Codex.SettingsPath = Path.Combine(_tempDir, "codex-config.toml");
        config.Codex.EnvPath = Path.Combine(_tempDir, "codex-env");
        return config;
    }

    [Fact]
    public void CodexSettingsSyncAddsSectionAndKeepsEveryOtherLine()
    {
        var config = CodexConfig();
        File.WriteAllText(config.Codex.SettingsPath, """
            notify = [ "C:\\tools\\codex-computer-use.exe", "turn-ended" ]

            [desktop]
            followUpQueueMode = "steer"

            [mcp_servers.node_repl]
            args = []
            command = 'C:\tools\node_repl.exe'
            """);
        var guard = new CodexSettingsGuard(config);
        Assert.Equal(GuardState.Partial, guard.Read().State); // the flag is not set yet

        var result = guard.Sync();

        Assert.True(result.Written);
        Assert.NotNull(result.BackupPath);
        Assert.True(File.Exists(result.BackupPath));
        Assert.Equal(GuardState.On, guard.Read().State);

        var lines = File.ReadAllLines(config.Codex.SettingsPath);
        Assert.Contains("[features]", lines);
        Assert.Contains("respect_system_proxy = true", lines);
        var before = File.ReadAllLines(result.BackupPath!);
        Assert.All(before, line => Assert.Contains(line, lines)); // every original line survived byte for byte
        Assert.Equal(before.Length + 3, lines.Length); // blank line, section header, key

        // the second sync finds the flag already correct: no rewrite, no backup
        var again = guard.Sync();
        Assert.False(again.Written);
        Assert.Null(again.BackupPath);
    }

    [Fact]
    public void CodexSettingsSyncInsertsIntoTheExistingSection()
    {
        var config = CodexConfig();
        File.WriteAllText(config.Codex.SettingsPath, "[features]\nfoo = \"bar\"\n\n[other]\nkey = 1\n");

        var result = new CodexSettingsGuard(config).Sync();

        Assert.True(result.Written);
        Assert.Equal(
            "[features]\nrespect_system_proxy = true\nfoo = \"bar\"\n\n[other]\nkey = 1\n",
            File.ReadAllText(config.Codex.SettingsPath));
    }

    [Fact]
    public void CodexSettingsSyncUpdatesTheFlagInPlaceKeepingStyle()
    {
        var config = CodexConfig();
        File.WriteAllText(config.Codex.SettingsPath, "[features]\r\nrespect_system_proxy = false  # let the app decide\r\nother = 1\r\n");
        var guard = new CodexSettingsGuard(config);
        Assert.Equal(GuardState.Partial, guard.Read().State);

        var result = guard.Sync();

        Assert.True(result.Written);
        Assert.Equal(
            "[features]\r\nrespect_system_proxy = true  # let the app decide\r\nother = 1\r\n",
            File.ReadAllText(config.Codex.SettingsPath)); // the inline comment and the CRLF style survive
        Assert.Equal(GuardState.On, guard.Read().State);
    }

    [Fact]
    public void CodexSettingsSyncRewritesTheDottedFlagAndIgnoresStrings()
    {
        var config = CodexConfig();
        var toml = "instructions = \"\"\"\n[features]\nrespect_system_proxy = true\n\"\"\"\nfeatures.respect_system_proxy = false\n";
        File.WriteAllText(config.Codex.SettingsPath, toml);
        var guard = new CodexSettingsGuard(config);

        // the lines inside the multi-line string are not structure: the effective value is the dotted key's false
        Assert.Equal(GuardState.Partial, guard.Read().State);

        var result = guard.Sync();

        Assert.True(result.Written);
        Assert.Equal(
            "instructions = \"\"\"\n[features]\nrespect_system_proxy = true\n\"\"\"\nfeatures.respect_system_proxy = true\n",
            File.ReadAllText(config.Codex.SettingsPath)); // the dotted key is rewritten, the string is untouched
        Assert.Equal(GuardState.On, guard.Read().State);
    }

    [Fact]
    public void CodexSettingsSyncCreatesTheFileAndStatusDetectsStates()
    {
        var config = CodexConfig();
        var guard = new CodexSettingsGuard(config);

        var missing = guard.Read();
        Assert.True(missing.FileMissing);
        Assert.Equal(GuardState.Off, missing.State);

        var created = guard.Sync();
        Assert.True(created.Written);
        Assert.Equal(
            new[] { "[features]", "respect_system_proxy = true" },
            File.ReadAllLines(config.Codex.SettingsPath));
        Assert.Equal(GuardState.On, guard.Read().State);

        File.WriteAllText(config.Codex.SettingsPath, "[desktop]\nfollowUpQueueMode = \"steer\"\n");
        var unset = guard.Read();
        Assert.Equal(GuardState.Partial, unset.State); // the file exists, the flag does not
        Assert.Null(unset.Enabled);
        Assert.False(unset.FileMissing);
    }

    // ----- .env of Codex (the file the embedded core loads itself) -----

    [Fact]
    public void CodexEnvApplyMergesKeysAndKeepsForeignLines()
    {
        var values = new Dictionary<string, string>
        {
            ["HTTP_PROXY"] = "http://127.0.0.1:18081",
            ["HTTPS_PROXY"] = "http://127.0.0.1:18081",
            ["ALL_PROXY"] = "http://127.0.0.1:18081",
            ["NO_PROXY"] = "localhost,127.0.0.1",
        };

        // a fresh file: just our keys
        Assert.Equal(
            "HTTP_PROXY=http://127.0.0.1:18081\nHTTPS_PROXY=http://127.0.0.1:18081\nALL_PROXY=http://127.0.0.1:18081\nNO_PROXY=localhost,127.0.0.1\n",
            CodexEnvFile.Apply(null, values));

        // existing keys are rewritten in place (the export form too), foreign lines and CRLF survive
        Assert.Equal(
            "# user comment\r\nHTTP_PROXY=http://127.0.0.1:18081\r\nMY_VAR=keep\r\nNO_PROXY=localhost,127.0.0.1\r\nHTTPS_PROXY=http://127.0.0.1:18081\r\nALL_PROXY=http://127.0.0.1:18081\r\n",
            CodexEnvFile.Apply("# user comment\r\nexport HTTP_PROXY=http://old:1\r\nMY_VAR=keep\r\nNO_PROXY=stale\r\n", values));

        // missing keys are appended, not duplicated
        var merged = CodexEnvFile.Apply("HTTP_PROXY=http://127.0.0.1:18081\n", values);
        Assert.Single(merged.Split('\n'), line => line.StartsWith("HTTP_PROXY=", StringComparison.Ordinal));
        Assert.Contains("\nALL_PROXY=http://127.0.0.1:18081\n", merged);

        // the status check: every key with the expected value
        Assert.True(CodexEnvFile.UpToDate(CodexEnvFile.Apply(null, values), values));
        Assert.False(CodexEnvFile.UpToDate("HTTP_PROXY=http://127.0.0.1:18081\n", values));
        Assert.False(CodexEnvFile.UpToDate(null, values));
    }

    [Fact]
    public void CodexEnvUpToDateAcceptsTheWslGatewayForm()
    {
        var values = new Dictionary<string, string>
        {
            ["HTTP_PROXY"] = "http://127.0.0.1:18081",
            ["HTTPS_PROXY"] = "http://127.0.0.1:18081",
            ["ALL_PROXY"] = "http://127.0.0.1:18081",
            ["NO_PROXY"] = "localhost,127.0.0.1",
        };

        static string GatewayEnv(string host, string noProxy = "localhost,127.0.0.1", int port = 18081) =>
            $"HTTP_PROXY=http://{host}:{port}\nHTTPS_PROXY=http://{host}:{port}\nALL_PROXY=http://{host}:{port}\nNO_PROXY={noProxy}\n";

        // what a WSL codex shell leaves behind: the NAT gateway form — the file rests, not stale
        Assert.True(CodexEnvFile.UpToDate(GatewayEnv("172.20.0.1"), values));

        // a foreign port, a public host or a drifted NO_PROXY is still a mismatch
        Assert.False(CodexEnvFile.UpToDate(GatewayEnv("172.20.0.1", port: 2081), values));
        Assert.False(CodexEnvFile.UpToDate(GatewayEnv("192.0.2.1"), values));
        Assert.False(CodexEnvFile.UpToDate(GatewayEnv("172.20.0.1", noProxy: "localhost"), values));
    }

    [Fact]
    public void CodexSettingsSyncWritesTheEnvFileWithBackupAndIdempotence()
    {
        var config = CodexConfig();
        File.WriteAllText(config.Codex.EnvPath, "HTTP_PROXY=http://elsewhere:3\nMY_VAR=keep\n");
        var guard = new CodexSettingsGuard(config);
        var before = guard.Read();
        Assert.Equal(GuardState.Partial, before.State);
        Assert.False(before.EnvUpToDate);
        Assert.Equal("config.toml not found; .env proxy env stale", before.Detail);

        var result = guard.Sync();

        Assert.True(result.EnvWritten);
        Assert.NotNull(result.EnvBackupPath);
        Assert.True(File.Exists(result.EnvBackupPath));
        var text = File.ReadAllText(config.Codex.EnvPath);
        Assert.Contains("HTTP_PROXY=http://127.0.0.1:18081\n", text);
        Assert.Contains("MY_VAR=keep\n", text); // the foreign line survives
        var after = guard.Read();
        Assert.True(after.EnvUpToDate);

        // the second sync finds the file already correct: no rewrite, no backup
        var again = guard.Sync();
        Assert.False(again.EnvWritten);
        Assert.Null(again.EnvBackupPath);
    }

    [Fact]
    public void CodexEnvWriteRefreshesAStaleFileForTheLauncher()
    {
        var config = CodexConfig();
        // what a WSL codex shell leaves behind: the NAT gateway address
        File.WriteAllText(config.Codex.EnvPath, "HTTP_PROXY=http://192.0.2.1:18081\nHTTPS_PROXY=http://192.0.2.1:18081\nALL_PROXY=http://192.0.2.1:18081\nNO_PROXY=localhost\n");

        var (written, backup) = CodexEnvFile.Write(config);

        Assert.True(written);
        Assert.NotNull(backup);
        Assert.Equal(
            "HTTP_PROXY=http://127.0.0.1:18081\nHTTPS_PROXY=http://127.0.0.1:18081\nALL_PROXY=http://127.0.0.1:18081\nNO_PROXY=localhost,127.0.0.1,.local\n",
            File.ReadAllText(config.Codex.EnvPath));
        var (again, noBackup) = CodexEnvFile.Write(config);
        Assert.False(again);
        Assert.Null(noBackup);
    }

    // ----- NRPT/firewall PowerShell scripts -----

    [Fact]
    public void NrptScriptCoversBothFormsAndEscapesQuotes()
    {
        var config = new AppConfig();
        config.Guard.NrptRuleName = "ai'direct";
        config.Claude.BanTrees = ["claude.ai"];
        config.Codex.BanTrees = ["openai.com"];
        var ops = new PowerShellBanSystemOps(config, new StubPowerShell());

        var script = ops.BuildNrptScript(apply: true);
        Assert.Contains("Remove-DnsClientNrptRule", script); // idempotence: remove first
        Assert.Contains("-Namespace @('claude.ai','.claude.ai','openai.com','.openai.com')", script);
        Assert.Contains("-NameServers @('127.0.0.1')", script);
        Assert.Contains("-DisplayName 'ai''direct'", script);
        Assert.DoesNotContain("Add-DnsClientNrptRule", ops.BuildNrptScript(apply: false));
    }

    [Fact]
    public void FirewallScriptSplitsFamiliesAndUsesGroup()
    {
        var config = new AppConfig();
        config.Claude.FirewallSubnets = ["160.79.104.0/21", "2607:6bc0::/48", "2607:6bc0:11::/48"];
        var ops = new PowerShellBanSystemOps(config, new StubPowerShell());

        var script = ops.BuildFirewallScript(apply: true);
        Assert.Contains("Remove-NetFirewallRule", script);
        Assert.Contains("-DisplayName 'ai-direct-ban: IPv4' -Group 'ai-direct-ban'", script);
        Assert.Contains("-RemoteAddress @('160.79.104.0/21')", script);
        Assert.Contains("-DisplayName 'ai-direct-ban: IPv6'", script);
        Assert.Contains("-RemoteAddress @('2607:6bc0::/48','2607:6bc0:11::/48')", script);
    }

    [Fact]
    public void LayersStateParsesSingleAndArrayJson()
    {
        var single = PowerShellBanSystemOps.ParseLayersState("""{"nrptCount":1,"ns":"claude.ai","fwCount":0,"subnets":null}""");
        Assert.Equal(1, single.NrptRules);
        Assert.Equal(["claude.ai"], single.NrptNamespaces);
        Assert.Empty(single.FirewallSubnets);

        var array = PowerShellBanSystemOps.ParseLayersState("""{"nrptCount":1,"ns":[".claude.ai","claude.ai"],"fwCount":2,"subnets":["160.79.104.0/21","2607:6bc0::/48"]}""");
        Assert.Equal(2, array.NrptNamespaces.Count);
        Assert.Equal(2, array.FirewallRules);
    }

    private sealed class StubPowerShell : IPowerShellExecutor
    {
        public Task<(int Code, string Output)> RunScriptAsync(string script, CancellationToken ct) => Task.FromResult((0, ""));
    }

    // ----- WSL layer -----

    private AppConfig WslConfig(string mode, params (string Name, string User)[] distros)
        => WslConfig(mode, syncClaude: true, syncCodex: true, distros);

    private AppConfig WslConfig(string mode, bool syncClaude, bool syncCodex, params (string Name, string User)[] distros)
    {
        var config = new AppConfig();
        config.ForwardProxy.Listen = "127.0.0.1:18082"; // the assertions expect the documented port, not the moving default
        config.Wsl.Mode = mode;
        config.Wsl.SyncClaude = syncClaude;
        config.Wsl.SyncCodex = syncCodex;
        config.Wsl.Distros = distros
            .Select(d => new WslDistroConfig { Name = d.Name, User = d.User })
            .ToList();
        return config;
    }

    /// <summary>Canned probe answer of a distro whose layers match the config (port 18082).</summary>
    private static string WslProbeInSync(string user, string proxy = "http://192.0.2.1:18082") =>
        $"SCRIPT=present\nPORT=18082\nUSER_NAME='{user}'\n"
        + "PROXYENV=present\nPNR_CODEX_PROXY_PORT=18082\n"
        + $"SETTINGS=present\n{{\"env\":{{\"HTTPS_PROXY\":\"{proxy}\"}}}}\nSETTINGS=END\n";

    [Fact]
    public async Task WslLayerModes()
    {
        var off = new WslConfigGuard(WslConfig("off", ("d1", "u1")), new FakeWslRunner());
        var (enabled, reason) = await off.EvaluateLayerAsync(CancellationToken.None);
        Assert.False(enabled);
        Assert.Contains("mode=off", reason);

        var empty = new WslConfigGuard(WslConfig("on"), new FakeWslRunner());
        Assert.False((await empty.EvaluateLayerAsync(CancellationToken.None)).Enabled);

        var runner = new FakeWslRunner { Installed = ["d1", "d2"], Running = ["d1"] };
        var on = new WslConfigGuard(WslConfig("on", ("d9", "u9")), runner);
        Assert.True((await on.EvaluateLayerAsync(CancellationToken.None)).Enabled); // on does not look at installation

        var auto = new WslConfigGuard(WslConfig("auto", ("d9", "u9")), runner);
        Assert.False((await auto.EvaluateLayerAsync(CancellationToken.None)).Enabled); // d9 is not installed

        var autoMatch = new WslConfigGuard(WslConfig("auto", ("d1", "u1")), runner);
        Assert.True((await autoMatch.EvaluateLayerAsync(CancellationToken.None)).Enabled);

        // both syncs off — there is nothing to maintain, the layer reports off
        var bothOff = new WslConfigGuard(
            WslConfig("on", syncClaude: false, syncCodex: false, ("d1", "u1")), runner);
        var (enabledBoth, reasonBoth) = await bothOff.EvaluateLayerAsync(CancellationToken.None);
        Assert.False(enabledBoth);
        Assert.Contains("nothing to sync", reasonBoth);
    }

    [Fact]
    public async Task WslStatusAggregatesPerDistro()
    {
        var runner = new FakeWslRunner
        {
            Installed = ["d1", "d2"],
            Running = ["d1"], // d2 is not running; not started just for status
            ProbeOutput = distro => distro == "d1" ? WslProbeInSync("u1") : "",
        };
        var guard = new WslConfigGuard(
            WslConfig("on", ("d1", "u1"), ("d2", "u2")),
            runner,
            gatewayProvider: () => IPAddress.Parse("192.0.2.1"));

        var status = await guard.ReadAsync(CancellationToken.None);
        Assert.Equal(GuardState.Unknown, status.State); // d2 is not running, so unknown
        Assert.Equal(2, status.Distros.Count);
        Assert.Equal(GuardState.On, status.Distros[0].State);
        Assert.Equal(18082, status.Distros[0].ScriptPort);
        Assert.Equal("http://192.0.2.1:18082", status.Distros[0].CurrentProxy);
        Assert.True(status.Distros[0].ProxyEnvPresent);
        Assert.Equal(18082, status.Distros[0].ProxyEnvPort);
        Assert.Equal(GuardState.Unknown, status.Distros[1].State);
        Assert.Contains("not running", status.Distros[1].Reason);
    }

    [Fact]
    public async Task WslStatusReportsCodexProxyEnvState()
    {
        // d1 carries the codex env file, d2 has none (the layer is on for both)
        var runner = new FakeWslRunner
        {
            Installed = ["d1", "d2"],
            Running = ["d1", "d2"],
            ProbeOutput = distro => distro == "d1"
                ? WslProbeInSync("u1")
                : "SCRIPT=present\nPORT=18082\nUSER_NAME='u2'\nPROXYENV=missing\n"
                  + "SETTINGS=present\n{\"env\":{\"HTTPS_PROXY\":\"http://192.0.2.1:18082\"}}\nSETTINGS=END\n",
        };
        var guard = new WslConfigGuard(
            WslConfig("on", ("d1", "u1"), ("d2", "u2")),
            runner,
            gatewayProvider: () => IPAddress.Parse("192.0.2.1"));

        var status = await guard.ReadAsync(CancellationToken.None);

        Assert.Equal(GuardState.Partial, status.State);
        Assert.Equal(GuardState.On, status.Distros[0].State);
        Assert.Equal("", status.Distros[0].Reason);
        Assert.Equal(GuardState.Partial, status.Distros[1].State);
        Assert.Contains("codex proxy env file not found", status.Distros[1].Reason);
    }

    [Fact]
    public async Task WslStatusIgnoresCodexProxyEnvWhenSyncCodexIsOff()
    {
        var runner = new FakeWslRunner
        {
            Installed = ["d1"],
            Running = ["d1"],
            ProbeOutput = _ => "SCRIPT=present\nPORT=18082\nUSER_NAME='u1'\nPROXYENV=missing\n"
                + "SETTINGS=present\n{\"env\":{\"HTTPS_PROXY\":\"http://192.0.2.1:18082\"}}\nSETTINGS=END\n",
        };
        var config = WslConfig("on", syncClaude: true, syncCodex: false, ("d1", "u1"));
        var guard = new WslConfigGuard(config, runner, gatewayProvider: () => IPAddress.Parse("192.0.2.1"));

        var status = await guard.ReadAsync(CancellationToken.None);

        Assert.Equal(GuardState.On, status.Distros[0].State);
    }

    [Fact]
    public void CodexProxyEnvScriptExportsTheProxyToCodexShellsOnly()
    {
        var script = WslConfigGuard.BuildCodexProxyEnvScript(18082, "localhost,127.0.0.1,.local,corp.example.com,.corp.example.com");

        // the marker the desktop app always passes into the distro for its own processes
        Assert.Contains("if [ -n \"${CODEX_HOME:-}\" ]; then", script);
        // the fallback: a shell started by hand to run codex
        Assert.Contains("case \"${BASH_EXECUTION_STRING:-}\" in", script);
        Assert.Contains("*codex*|*Codex*)", script);
        // the address is the WSL NAT gateway recomputed at every shell start, not 127.0.0.1
        Assert.Contains("ip route show default", script);
        Assert.Contains("export HTTP_PROXY=\"http://$_pnr_host_ip:$PNR_CODEX_PROXY_PORT\"", script);
        Assert.Contains("export NO_PROXY=\"localhost,127.0.0.1,.local,corp.example.com,.corp.example.com\"", script);
        // the port line is what `pnr guard status` reads back
        Assert.Contains("PNR_CODEX_PROXY_PORT=18082", script);
        Assert.DoesNotContain("127.0.0.1:18082", script);
        // the .env of CODEX_HOME is rewritten to the gateway address: the Codex core loads it over
        // the inherited environment, so the exports alone would be overridden by the loopback form
        Assert.Contains("_pnr_env=\"$CODEX_HOME/.env\"", script);
        Assert.Contains("grep -q \"^HTTP_PROXY=http://$_pnr_host_ip:$PNR_CODEX_PROXY_PORT$\"", script);
        Assert.Contains("print \"HTTP_PROXY=\" _proxy", script);
        Assert.Contains("print \"NO_PROXY=\" _no_proxy", script);
        Assert.Contains("cat \"$_pnr_tmp\" > \"$_pnr_env\"", script);
    }

    [Fact]
    public async Task WslStatusDetectsPortMismatchAndMissingScript()
    {
        var runner = new FakeWslRunner
        {
            Installed = ["d1", "d3"],
            Running = ["d1", "d3"],
            ProbeOutput = distro => distro == "d1"
                ? "SCRIPT=present\nPORT=8082\nSETTINGS=missing\n"
                : "SCRIPT=missing\nSETTINGS=missing\n",
        };
        var guard = new WslConfigGuard(
            WslConfig("on", ("d1", "u1"), ("d3", "u3")),
            runner,
            gatewayProvider: () => IPAddress.Parse("192.0.2.1"));

        var status = await guard.ReadAsync(CancellationToken.None);
        Assert.Equal(GuardState.Partial, status.State);
        Assert.Equal(GuardState.Partial, status.Distros[0].State);
        Assert.Contains("script PORT=8082", status.Distros[0].Reason);
        Assert.Equal(GuardState.Off, status.Distros[1].State); // no script
    }

    [Fact]
    public async Task WslSyncVisitsEveryConfiguredDistroAndSkipsTheNotRunning()
    {
        var runner = new FakeWslRunner
        {
            Installed = ["d1", "d2", "d3"],
            Running = ["d1", "d3"], // d2 is not running, skip without bringing it up
            ProbeOutput = _ => "SCRIPT=present\nPORT=8082\nSETTINGS=present\n{}\nSETTINGS=END\n",
        };
        var guard = new WslConfigGuard(
            WslConfig("on", ("d1", "u1"), ("d2", "u2"), ("d3", "u3")),
            runner,
            gatewayProvider: () => IPAddress.Parse("192.0.2.1"));

        var result = await guard.SyncAsync(CancellationToken.None);

        Assert.False(result.LayerDisabled);
        Assert.Equal(3, result.Distros.Count); // every configured distro is visited; not-running ones are skipped without being started
        var d1 = Assert.Single(result.Distros, d => d.Distro == "d1");
        var d2 = Assert.Single(result.Distros, d => d.Distro == "d2");
        Assert.False(d1.Skipped);
        Assert.Equal("http://192.0.2.1:18082", d1.ProxyUrl);
        Assert.True(d2.Skipped);
        Assert.Contains("not running", d2.Reason);

        // d1: one pass as the user (settings) and two as root (startup script + codex proxy env)
        var settings = Assert.Single(runner.Scripts, s => s.Distro == "d1" && s.User == "u1");
        Assert.Contains("SETTINGS=\"$HOME/.claude/settings.json\"", settings.Script);
        Assert.Contains("pnr-bak", settings.Script);
        Assert.Contains("pnr_bak \"$SETTINGS\" BACKUP", settings.Script); // the deduplicating backup helper
        Assert.DoesNotContain("cp -p \"$SETTINGS\"", settings.Script);
        var root = Assert.Single(runner.Scripts, s => s.Distro == "d1" && s.User == "root" && s.Script.Contains("10-claude-proxy.sh"));
        Assert.Contains("/etc/wsl.d/startup/10-claude-proxy.sh", root.Script);
        Assert.Contains("pnr_bak \"$SCRIPT\" BACKUP", root.Script);
        Assert.DoesNotContain("chmod 755", settings.Script);
        Assert.True(d1.SettingsWritten);
        Assert.True(d1.ScriptWritten);
        Assert.True(d1.ProxyEnvWritten);

        // startup script contents: the app port and the distro user
        var payload = ExtractBase64Payload(root.Script, "SCRIPT=\"/etc/wsl.d/startup/10-claude-proxy.sh\"");
        var script = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
        Assert.Contains("USER_NAME='u1'", script);
        Assert.Contains("PORT=18082", script);
        Assert.Contains("env['HTTPS_PROXY'] = proxy", script);
        Assert.DoesNotContain("env['NO_PROXY']", script); // the startup script does not touch NO_PROXY (mentioned only in a comment)

        // codex proxy env contents: the marker, the port and the bypass zones of the config
        var envPass = Assert.Single(runner.Scripts, s => s.Distro == "d1" && s.User == "root" && s.Script.Contains("ENVFILE="));
        Assert.Contains("pnr_bak \"$ENVFILE\" BACKUP", envPass.Script);
        var envPayload = ExtractBase64Payload(envPass.Script, "ENVFILE=\"/etc/profile.d/20-pnr-codex-proxy.sh\"");
        var envScript = Encoding.UTF8.GetString(Convert.FromBase64String(envPayload));
        Assert.Contains("wsl.proxyEnvPath", envScript);
        Assert.Contains("PNR_CODEX_PROXY_PORT=18082", envScript);
        Assert.Contains("${CODEX_HOME:-}", envScript);
        Assert.Contains("export NO_PROXY=", envScript);
        // d3 is running: it is visited and written like d1
        var d3 = Assert.Single(result.Distros, d => d.Distro == "d3");
        Assert.False(d3.Skipped);
        Assert.True(d3.SettingsWritten);
    }

    [Fact]
    public async Task WslSyncLeavesCodexProxyEnvAloneWhenSyncCodexIsOff()
    {
        var runner = new FakeWslRunner { Installed = ["d1"], Running = ["d1"] };
        var config = WslConfig("on", syncClaude: true, syncCodex: false, ("d1", "u1"));
        var guard = new WslConfigGuard(config, runner, gatewayProvider: () => IPAddress.Parse("192.0.2.1"));

        var result = await guard.SyncAsync(CancellationToken.None);

        var d1 = Assert.Single(result.Distros);
        Assert.False(d1.ProxyEnvWritten);
        Assert.DoesNotContain(runner.Scripts, s => s.Script.Contains("ENVFILE="));
    }

    [Fact]
    public async Task WslSyncReadsTheCodexProxyEnvBackupFromDistroOutput()
    {
        var runner = new FakeWslRunner
        {
            Installed = ["d1"],
            Running = ["d1"],
            WriteOutput = """
                SETTINGS=unchanged
                SCRIPT=unchanged
                BACKUP=/etc/profile.d/20-pnr-codex-proxy.sh.pnr-bak-20261013-120000
                ENVFILE=written
                PNR=ok
                """,
        };
        var guard = new WslConfigGuard(
            WslConfig("on", ("d1", "u1")),
            runner,
            gatewayProvider: () => IPAddress.Parse("192.0.2.1"));

        var result = await guard.SyncAsync(CancellationToken.None);

        var d1 = Assert.Single(result.Distros);
        Assert.False(d1.SettingsWritten);
        Assert.False(d1.ScriptWritten);
        Assert.True(d1.ProxyEnvWritten);
        Assert.Equal("/etc/profile.d/20-pnr-codex-proxy.sh.pnr-bak-20261013-120000", d1.ProxyEnvBackup);
    }

    [Fact]
    public async Task WslSyncSkipsWholeLayerWhenDisabled()
    {
        var runner = new FakeWslRunner { Installed = ["d1"], Running = ["d1"] };
        var guard = new WslConfigGuard(WslConfig("off", ("d1", "u1")), runner);

        var result = await guard.SyncAsync(CancellationToken.None);
        Assert.True(result.LayerDisabled);
        Assert.Empty(runner.Scripts);
    }

    [Fact]
    public async Task WslSyncReadsBackupsAndUnchangedFlagsFromDistroOutput()
    {
        var runner = new FakeWslRunner
        {
            Installed = ["d1"],
            Running = ["d1"],
            WriteOutput = """
                SETTINGS=written
                BACKUP=settings.json.pnr-bak-20261012-190000
                SCRIPT=unchanged
                PNR=ok
                """,
        };
        var guard = new WslConfigGuard(
            WslConfig("on", ("d1", "u1")),
            runner,
            gatewayProvider: () => IPAddress.Parse("192.0.2.1"));

        var result = await guard.SyncAsync(CancellationToken.None);

        var d1 = Assert.Single(result.Distros);
        Assert.False(d1.Skipped);
        Assert.True(d1.SettingsWritten);
        Assert.Equal("settings.json.pnr-bak-20261012-190000", d1.SettingsBackup);
        Assert.False(d1.ScriptWritten); // the distro confirmed the script was already current
        Assert.Null(d1.ScriptBackup);
    }

    [Fact]
    public async Task WslSyncReportsUnchangedWhenDistroConfirmsNoChange()
    {
        var runner = new FakeWslRunner
        {
            Installed = ["d1"],
            Running = ["d1"],
            WriteOutput = "SETTINGS=unchanged\nSCRIPT=unchanged\nPNR=ok\n",
        };
        var guard = new WslConfigGuard(
            WslConfig("on", ("d1", "u1")),
            runner,
            gatewayProvider: () => IPAddress.Parse("192.0.2.1"));

        var result = await guard.SyncAsync(CancellationToken.None);

        var d1 = Assert.Single(result.Distros);
        Assert.False(d1.SettingsWritten);
        Assert.False(d1.ScriptWritten);
        Assert.Null(d1.SettingsBackup);
        Assert.Null(d1.ScriptBackup);
    }

    [Fact]
    public async Task WslSyncFailsLoudlyWhenDistroScriptDidNotRun()
    {
        // a bash syntax error inside the distro does not change the wsl.exe exit code
        var runner = new FakeWslRunner
        {
            Installed = ["d1"],
            Running = ["d1"],
            WriteOutput = "bash: line 3: syntax error near unexpected token\n",
        };
        var guard = new WslConfigGuard(
            WslConfig("on", ("d1", "u1")),
            runner,
            gatewayProvider: () => IPAddress.Parse("192.0.2.1"));

        var ex = await Assert.ThrowsAsync<IOException>(() => guard.SyncAsync(CancellationToken.None));
        Assert.Contains("did not run to the end", ex.Message);
        Assert.Contains("syntax error", ex.Message);
    }

    // ----- WSL boot layer (wsl.conf [boot] command → the marked runner) -----


    [Fact]
    public void WslBootLayerPlanAndWslConfEdits()
    {
        var runnerPath = "/etc/wsl.d/startup.sh";

        // the plan: recognize the already-done state, take over a foreign runner file,
        // preserve a foreign command, install on an empty slot
        Assert.Equal(WslBootPlan.AlreadyOurs, WslConfigGuard.PlanBootLayer(runnerPath, runnerPath, WslBootRunnerState.Ours));
        Assert.Equal(WslBootPlan.TakeoverRunner, WslConfigGuard.PlanBootLayer(runnerPath, runnerPath, WslBootRunnerState.Foreign));
        Assert.Equal(WslBootPlan.TakeoverRunner, WslConfigGuard.PlanBootLayer(runnerPath, runnerPath, WslBootRunnerState.Missing));
        Assert.Equal(WslBootPlan.InstallRunner, WslConfigGuard.PlanBootLayer(runnerPath, null, WslBootRunnerState.Missing));
        Assert.Equal(WslBootPlan.InstallRunner, WslConfigGuard.PlanBootLayer(runnerPath, "", WslBootRunnerState.Ours));
        Assert.Equal(WslBootPlan.AdoptForeignCommand, WslConfigGuard.PlanBootLayer(runnerPath, "docker start things", WslBootRunnerState.Missing));
        Assert.Equal(WslBootPlan.AdoptForeignCommand, WslConfigGuard.PlanBootLayer(runnerPath, "docker start things", WslBootRunnerState.Ours));

        // extraction: quoted and unquoted, case-insensitive; wrong section and comments ignored
        Assert.Equal(runnerPath, WslConfigGuard.ExtractBootCommand("[boot]\ncommand=\"/etc/wsl.d/startup.sh\"\n"));
        Assert.Equal(runnerPath, WslConfigGuard.ExtractBootCommand("[BOOT]\nCOMMAND = /etc/wsl.d/startup.sh\n"));
        Assert.Null(WslConfigGuard.ExtractBootCommand("[user]\ncommand=\"x\"\n"));
        Assert.Null(WslConfigGuard.ExtractBootCommand("[boot]\n# command=\"x\"\n"));
        Assert.Null(WslConfigGuard.ExtractBootCommand(null));

        // edit: surgical — the command line is replaced, everything else byte for byte
        var conf = "[boot]\nsystemd=true\ncommand=\"docker start things\"\n\n[user]\ndefault=u1\n";
        Assert.Equal(
            "[boot]\nsystemd=true\ncommand=\"/etc/wsl.d/startup.sh\"\n\n[user]\ndefault=u1\n",
            WslConfigGuard.SetBootCommand(conf, runnerPath));

        // edit: the key is inserted after [boot] when missing, the section appended when missing
        Assert.Equal(
            "[boot]\ncommand=\"/etc/wsl.d/startup.sh\"\nsystemd=true\n",
            WslConfigGuard.SetBootCommand("[boot]\nsystemd=true\n", runnerPath));
        Assert.EndsWith(
            "\n\n[boot]\ncommand=\"/etc/wsl.d/startup.sh\"\n",
            WslConfigGuard.SetBootCommand("[user]\ndefault=u1\n", runnerPath));
        Assert.Equal("[boot]\ncommand=\"/etc/wsl.d/startup.sh\"\n", WslConfigGuard.SetBootCommand(null, runnerPath));

        // paths are Linux-side: no separator rewriting
        Assert.Equal("/etc/wsl.d/startup.sh", WslConfigGuard.BootRunnerPath("/etc/wsl.d/startup/10-claude-proxy.sh"));
        Assert.Equal("/etc/wsl.d/startup/00-user-boot-command.sh", WslConfigGuard.UserCommandPath("/etc/wsl.d/startup/10-claude-proxy.sh"));
    }

    [Fact]
    public async Task WslSyncInstallsBootRunnerWhenCommandIsEmpty()
    {
        var runner = new FakeWslRunner
        {
            Installed = ["d1"],
            Running = ["d1"],
            ProbeOutput = _ => "SCRIPT=missing\nPROXYENV=missing\nRUNNER=missing\nWSLCONF=missing\nSETTINGS=missing\n",
            WriteOutput = "RUNNER=written\nWSLCONF=written\nSETTINGS=written\nSCRIPT=written\nENVFILE=written\nPNR=ok\n",
        };
        var guard = new WslConfigGuard(
            WslConfig("on", ("d1", "u1")),
            runner,
            gatewayProvider: () => IPAddress.Parse("192.0.2.1"));

        var result = await guard.SyncAsync(CancellationToken.None);

        var d1 = Assert.Single(result.Distros);
        Assert.True(d1.BootRunnerWritten);
        Assert.False(d1.UserCommandWritten);
        Assert.True(d1.WslConfWritten);

        var boot = Assert.Single(runner.Scripts, s => s.User == "root" && s.Script.Contains("RUNNER="));
        Assert.DoesNotContain("USERCMD=", boot.Script);
        Assert.Contains("pnr_bak \"$RUNNER\" BACKUP_RUNNER", boot.Script); // the deduplicating backup helper
        Assert.Contains("pnr_bak \"$WSLCONF\" BACKUP_WSLCONF", boot.Script);
        var runnerPayload = ExtractBase64Payload(boot.Script, "RUNNER=\"/etc/wsl.d/startup.sh\"");
        var runnerFile = Encoding.UTF8.GetString(Convert.FromBase64String(runnerPayload));
        Assert.StartsWith("#!/bin/bash", runnerFile);
        Assert.Contains(WslConfigGuard.BootRunnerMarker, runnerFile);
        Assert.Contains("SCRIPT_DIR=\"/etc/wsl.d/startup\"", runnerFile);
        Assert.Contains("for script in \"${files[@]}\"; do", runnerFile);
        Assert.Contains("*.pnr-bak-*|*.bak-*)", runnerFile); // backups in the startup dir never run
        Assert.DoesNotContain("\r", runnerFile); // the distro runs bash: LF only
        var confPayload = ExtractBase64Payload(boot.Script, "WSLCONF=\"/etc/wsl.conf\"");
        Assert.Equal(
            "[boot]\ncommand=\"/etc/wsl.d/startup.sh\"\n",
            Encoding.UTF8.GetString(Convert.FromBase64String(confPayload)));
    }

    [Fact]
    public void WslBuildersEmitLfOnlyScripts()
    {
        // Raw multiline literals take the line endings of the checkout; the builders must
        // normalize at the boundary (a CRLF checkout once put \r into every distro script,
        // and bash dies on the first line while wsl.exe still exits 0).
        Assert.DoesNotContain("\r", WslConfigGuard.BuildBootRunner("/etc/wsl.d/startup"));
        Assert.DoesNotContain("\r", WslConfigGuard.BuildUserCommandScript("docker start things"));
        Assert.DoesNotContain("\r", WslConfigGuard.BuildStartupScript("u1", 18081));
        Assert.DoesNotContain("\r", WslConfigGuard.BuildCodexProxyEnvScript(18081, "localhost,.corp.example.com"));
    }

    [Fact]
    public void WslLfBoundaryStripsCarriageReturnsFromAnyInput()
    {
        // The \r here are escapes: the mechanism itself is checked independently of the checkout.
        Assert.Equal("a\nb\nc", WslConfigGuard.NormalizeLf("a\r\nb\rc"));
        Assert.Equal("a\nb", Decode(WslConfigGuard.DistroPayload("a\r\nb")));
        Assert.Equal("a\nb", Decode(WslConfigGuard.DistroPayload("a\nb"))); // LF passes through unchanged
    }

    [Fact]
    public async Task WslSyncSendsLfOnlyScriptsAndPayloads()
    {
        var runner = new FakeWslRunner
        {
            Installed = ["d1"],
            Running = ["d1"],
            ProbeOutput = _ => "SCRIPT=missing\nPROXYENV=missing\nRUNNER=missing\nWSLCONF=missing\nSETTINGS=missing\n",
            WriteOutput = "RUNNER=written\nWSLCONF=written\nSETTINGS=written\nSCRIPT=written\nENVFILE=written\nPNR=ok\n",
        };
        var guard = new WslConfigGuard(
            WslConfig("on", ("d1", "u1")),
            runner,
            gatewayProvider: () => IPAddress.Parse("192.0.2.1"));

        await guard.SyncAsync(CancellationToken.None);

        // every script piped to bash -s (boot, settings, startup, codex env), and every base64
        // payload it embeds, is LF only — whatever the checkout the sources were compiled from
        Assert.Equal(4, runner.Scripts.Count);
        foreach (var (_, _, script) in runner.Scripts)
        {
            Assert.DoesNotContain("\r", script);
            foreach (var line in script.Split('\n'))
            {
                if (!line.Contains("base64 -d", StringComparison.Ordinal))
                {
                    continue;
                }

                var quote = line.IndexOf('\'');
                Assert.DoesNotContain("\r", Decode(line[(quote + 1)..line.IndexOf('\'', quote + 1)]));
            }
        }
    }

    private static string Decode(string base64) => Encoding.UTF8.GetString(Convert.FromBase64String(base64));

    [Fact]
    public async Task WslSyncPreservesForeignBootCommandAsTheFirstStartupScript()
    {
        var conf = "[boot]\nsystemd=true\ncommand=\"docker start things\"\n\n[user]\ndefault=u1\n";
        var runner = new FakeWslRunner
        {
            Installed = ["d1"],
            Running = ["d1"],
            ProbeOutput = _ => "SCRIPT=missing\nPROXYENV=missing\nRUNNER=missing\nWSLCONF=present\n"
                + conf + "\nWSLCONF=END\nSETTINGS=missing\n",
            WriteOutput = "RUNNER=written\nUSERCMD=written\nBACKUP_WSLCONF=/etc/wsl.conf.pnr-bak-20261014-120000\n"
                + "WSLCONF=written\nPNR=ok\n",
        };
        var guard = new WslConfigGuard(
            WslConfig("on", ("d1", "u1")),
            runner,
            gatewayProvider: () => IPAddress.Parse("192.0.2.1"));

        var result = await guard.SyncAsync(CancellationToken.None);

        var d1 = Assert.Single(result.Distros);
        Assert.True(d1.BootRunnerWritten);
        Assert.True(d1.UserCommandWritten);
        Assert.True(d1.WslConfWritten);
        Assert.Equal("/etc/wsl.conf.pnr-bak-20261014-120000", Assert.Single(d1.BootBackups ?? []));

        var boot = Assert.Single(runner.Scripts, s => s.User == "root" && s.Script.Contains("USERCMD="));
        var userPayload = ExtractBase64Payload(boot.Script, "USERCMD=\"/etc/wsl.d/startup/00-user-boot-command.sh\"");
        var userScript = Encoding.UTF8.GetString(Convert.FromBase64String(userPayload));
        Assert.EndsWith("docker start things", userScript); // the user's command, verbatim
        Assert.Equal("755", GetChmodTarget(boot.Script, "USERCMD="));

        var confPayload = ExtractBase64Payload(boot.Script, "WSLCONF=\"/etc/wsl.conf\"");
        var newConf = Encoding.UTF8.GetString(Convert.FromBase64String(confPayload));
        Assert.Equal(
            "[boot]\nsystemd=true\ncommand=\"/etc/wsl.d/startup.sh\"\n\n[user]\ndefault=u1\n",
            newConf); // the user's own lines survive byte for byte, only the command changes
    }

    [Fact]
    public async Task WslSyncRecognizesItsOwnBootLayerAndSkipsIt()
    {
        var conf = "[boot]\nsystemd=true\ncommand=\"/etc/wsl.d/startup.sh\"\n";
        var runner = new FakeWslRunner
        {
            Installed = ["d1"],
            Running = ["d1"],
            ProbeOutput = _ => "SCRIPT=present\nPORT=18082\nUSER_NAME='u1'\nPROXYENV=missing\nRUNNER=ours\n"
                + "WSLCONF=present\n" + conf + "\nWSLCONF=END\nSETTINGS=missing\n",
            WriteOutput = "SETTINGS=written\nSCRIPT=written\nPNR=ok\n",
        };
        var guard = new WslConfigGuard(
            WslConfig("on", ("d1", "u1")),
            runner,
            gatewayProvider: () => IPAddress.Parse("192.0.2.1"));

        var result = await guard.SyncAsync(CancellationToken.None);

        var d1 = Assert.Single(result.Distros);
        Assert.False(d1.BootRunnerWritten);
        Assert.False(d1.UserCommandWritten);
        Assert.False(d1.WslConfWritten);
        Assert.Empty(d1.BootBackups ?? []);
        // the wiring is not redone, but the runner file itself is still ensured (cmp → no-op here):
        // a boot pass runs, with the runner block only
        var boot = Assert.Single(runner.Scripts, s => s.User == "root" && s.Script.Contains("RUNNER="));
        Assert.DoesNotContain("USERCMD=", boot.Script);
        Assert.DoesNotContain("WSLCONF=", boot.Script);
    }

    [Fact]
    public async Task WslSyncTakesOverAForeignFileAtTheRunnerPath()
    {
        // the pre-application scheme: the command already points at the runner path,
        // but the file there is a foreign wrapper without the marker — replaced, not wrapped
        var conf = "[boot]\ncommand=\"/etc/wsl.d/startup.sh\"\n";
        var runner = new FakeWslRunner
        {
            Installed = ["d1"],
            Running = ["d1"],
            ProbeOutput = _ => "SCRIPT=missing\nPROXYENV=missing\nRUNNER=foreign\nWSLCONF=present\n"
                + conf + "\nWSLCONF=END\nSETTINGS=missing\n",
            WriteOutput = "RUNNER=written\nPNR=ok\n",
        };
        var guard = new WslConfigGuard(
            WslConfig("on", ("d1", "u1")),
            runner,
            gatewayProvider: () => IPAddress.Parse("192.0.2.1"));

        var result = await guard.SyncAsync(CancellationToken.None);

        var d1 = Assert.Single(result.Distros);
        Assert.True(d1.BootRunnerWritten);
        Assert.False(d1.UserCommandWritten);
        Assert.False(d1.WslConfWritten);
        var boot = Assert.Single(runner.Scripts, s => s.User == "root" && s.Script.Contains("RUNNER="));
        Assert.DoesNotContain("USERCMD=", boot.Script);
        Assert.DoesNotContain("WSLCONF=", boot.Script);
    }

    /// <summary>The path chmod is applied to in a boot-layer write block (by its echo marker).</summary>
    private static string GetChmodTarget(string script, string marker)
    {
        var index = script.IndexOf(marker, StringComparison.Ordinal);
        var line = script[index..].Split('\n').First(l => l.TrimStart().StartsWith("chmod", StringComparison.Ordinal));
        return line.Trim().Split(' ')[1];
    }

    private static string ExtractBase64Payload(string script, string afterMarker)
    {
        // echo '<b64>' | base64 -d > <path>
        var index = script.IndexOf(afterMarker, StringComparison.Ordinal);
        Assert.True(index >= 0, $"marker {afterMarker} not found");
        var line = script[index..].Split('\n').First(l => l.Contains("base64 -d"));
        var quote = line.IndexOf('\'');
        return line[(quote + 1)..line.IndexOf('\'', quote + 1)];
    }

    // ----- GuardService orchestration -----

    private GuardService MakeService(
        AppConfig config,
        FakeEgressPolicyStore policy,
        FakeBanSystemOps banOps,
        FakeSystemProxyStore sysProxy,
        FakeElevatedRunner elevated,
        out FileLog log,
        Func<CancellationToken, Task<bool>>? chainHealth = null)
    {
        log = new FileLog(Path.Combine(_tempDir, $"guard-{Guid.NewGuid():N}.log"));
        _logs.Add(log);
        return new GuardService(
            config,
            policy,
            banOps,
            new ClaudeSettingsGuard(config),
            new CodexSettingsGuard(config),
            new WslConfigGuard(config, new FakeWslRunner(), () => IPAddress.Parse("192.0.2.1")),
            sysProxy,
            elevated,
            log,
            chainHealth ?? (_ => Task.FromResult(false)));
    }

    [Fact]
    public async Task StatusAggregatesLayersAndPolicies()
    {
        var config = new AppConfig();
        config.ForwardProxy.Listen = "127.0.0.1:18082"; // pinned: the assertions expect the documented port
        config.Guard.HostsFile = Path.Combine(_tempDir, "hosts");
        config.Guard.BanProbes = ["probe.invalid"];
        File.WriteAllText(config.Guard.HostsFile, "127.0.0.1 localhost\r\n");
        var banOps = new FakeBanSystemOps
        {
            Layers = new BanLayersState(1, [".claude.ai"], 2, ["160.79.104.0/21", "2607:6bc0::/48"]),
        };
        HostsFileBlock.Apply(config.Guard.HostsFile, config.Guard.HostsBeginMarker, config.Guard.HostsEndMarker, config.Guard.HostsComment, config.BanHosts);
        var service = MakeService(
            config,
            new FakeEgressPolicyStore { Values = new EgressPolicyValues(true, "http://127.0.0.1:18082", 0) },
            banOps,
            new FakeSystemProxyStore { Status = new SystemProxyStatus(true, "127.0.0.1:18082", "localhost") },
            new FakeElevatedRunner(),
            out _);

        var snapshot = await service.GetStatusAsync(CancellationToken.None);
        Assert.Equal(GuardState.On, snapshot.Ban.State);
        Assert.Equal(config.BanHosts.Count, snapshot.Ban.HostsEntries);
        Assert.True(snapshot.Ban.HostsCurrent);
        Assert.Equal(GuardState.On, snapshot.Policy.State);
        Assert.Equal(GuardState.On, snapshot[GuardKind.SystemProxy].State);
        Assert.Contains("NRPT: 1 rule(s) on 1 namespaces", snapshot[GuardKind.LocalBan].Detail);
        Assert.Contains("egressProxyUrl=http://127.0.0.1:18082", snapshot[GuardKind.EgressPolicy].Detail);
    }

    [Fact]
    public async Task StatusDetectsPartialBanAndWrongPolicy()
    {
        var config = new AppConfig();
        config.ForwardProxy.Listen = "127.0.0.1:18082"; // pinned: the assertions expect the documented port
        config.Guard.HostsFile = Path.Combine(_tempDir, "hosts-empty");
        config.Guard.BanProbes = ["probe.invalid"];
        var service = MakeService(
            config,
            new FakeEgressPolicyStore { Values = new EgressPolicyValues(true, "http://10.0.0.9:1", 0) },
            new FakeBanSystemOps { Layers = new BanLayersState(1, [".claude.ai"], 0, []) }, // no firewall
            new FakeSystemProxyStore(),
            new FakeElevatedRunner(),
            out _);

        var snapshot = await service.GetStatusAsync(CancellationToken.None);
        Assert.Equal(GuardState.Partial, snapshot.Ban.State);
        Assert.Equal(GuardState.Partial, snapshot.Policy.State);
        Assert.Contains("expected http://127.0.0.1:18082", snapshot[GuardKind.EgressPolicy].Detail);
        Assert.Equal(GuardState.Off, snapshot[GuardKind.SystemProxy].State);
    }

    [Fact]
    public async Task BanOnRunsElevatedCommandAndLiveChecks()
    {
        var config = new AppConfig();
        config.Guard.HostsFile = Path.Combine(_tempDir, "hosts-x");
        config.Guard.BanProbes = ["probe.invalid"]; // does not resolve, so the checks are deterministic
        var elevated = new FakeElevatedRunner();
        var healthCalls = 0;
        var service = MakeService(config, new FakeEgressPolicyStore(), new FakeBanSystemOps(), new FakeSystemProxyStore(), elevated,
            out _, _ => { healthCalls++; return Task.FromResult(false); });

        var result = await service.BanAsync(enable: true, CancellationToken.None);

        Assert.Equal(GuardElevatedCommands.BanOn, Assert.Single(elevated.Commands));
        Assert.Equal(1, healthCalls); // live check after applying (rule #6)
        Assert.True(result.Live.DnsBlocked);
        Assert.True(result.Live.DirectBlocked);
        Assert.Contains("skipped", result.Live.ProxiedDetail); // the chain does not answer, so skipped
    }

    [Fact]
    public async Task BanAndPolicyFailuresSurface()
    {
        var config = new AppConfig
        {
            Guard = new GuardConfig
            {
                HostsFile = Path.Combine(_tempDir, "h"),
                // a name that does not resolve, so the checks are deterministic
                BanProbes = ["probe.invalid"],
            },
        };
        var service = MakeService(config, new FakeEgressPolicyStore(), new FakeBanSystemOps(), new FakeSystemProxyStore(),
            new FakeElevatedRunner(ok: false, detail: "UAC declined"), out _);

        await Assert.ThrowsAsync<IOException>(() => service.BanAsync(true, CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(() => service.PolicyAsync(false, CancellationToken.None));
    }

    [Fact]
    public async Task AllRunsOneCombinedElevatedCommandWithLiveChecks()
    {
        var config = new AppConfig();
        config.Guard.HostsFile = Path.Combine(_tempDir, "hosts-all");
        config.Guard.BanProbes = ["probe.invalid"];
        var elevated = new FakeElevatedRunner();
        var healthCalls = 0;
        var service = MakeService(config, new FakeEgressPolicyStore(), new FakeBanSystemOps(), new FakeSystemProxyStore(), elevated,
            out _, _ => { healthCalls++; return Task.FromResult(false); });

        await service.AllAsync(enable: true, CancellationToken.None);
        await service.AllAsync(enable: false, CancellationToken.None);

        // one elevation per action, not one per layer — the collapsed card's point
        Assert.Equal([GuardElevatedCommands.AllOn, GuardElevatedCommands.AllOff], elevated.Commands);
        Assert.Equal(2, healthCalls); // live checks after each applying (rule #6)
    }

    [Fact]
    public async Task SystemProxyOnUsesListenerAddress()
    {
        var config = new AppConfig();
        config.ForwardProxy.Listen = "127.0.0.1:18082"; // pinned: the assertion expects the documented port
        var sysProxy = new FakeSystemProxyStore();
        var service = MakeService(config, new FakeEgressPolicyStore(), new FakeBanSystemOps(), sysProxy, new FakeElevatedRunner(), out _);

        await service.SetSystemProxyAsync(true, CancellationToken.None);
        Assert.Equal(("127.0.0.1:18082", SystemProxyBypass.Build([], config.DirectTlds)), sysProxy.EnabledWith);

        await service.SetSystemProxyAsync(false, CancellationToken.None);
        Assert.True(sysProxy.Disabled);
    }

    // ----- elevated helper: order of ban layers -----

    [Fact]
    public void ElevatedBanAppliesHostsThenNrptThenFirewallThenFlush()
    {
        var config = new AppConfig();
        config.Guard.HostsFile = Path.Combine(_tempDir, "hosts-elev");
        File.WriteAllText(config.Guard.HostsFile, "127.0.0.1 localhost\r\n");
        config.Guard.BanProbes = ["probe.invalid"];
        var ops = new FakeBanSystemOps();
        using var log = new FileLog(Path.Combine(_tempDir, "elev.log"));

        var detail = GuardElevatedCommands.ApplyBanLayers(config, log, ops, enable: true);

        Assert.Equal(["nrpt-on", "fw-on", "flush"], ops.Calls); // hosts is applied directly via the file
        var hosts = HostsFileBlock.Read(config.Guard.HostsFile, config.Guard.HostsBeginMarker, config.Guard.HostsEndMarker, config.Guard.HostsComment, config.BanHosts);
        Assert.True(hosts.Present);
        Assert.Equal(config.BanHosts.Count, hosts.EntryCount);
        Assert.Contains("NRPT rule on", detail);

        var removeDetail = GuardElevatedCommands.ApplyBanLayers(config, log, ops, enable: false);
        Assert.Equal(["nrpt-on", "fw-on", "flush", "nrpt-off", "fw-off", "flush"], ops.Calls);
        Assert.DoesNotContain(">>", File.ReadAllText(config.Guard.HostsFile));
        Assert.Contains("removed", removeDetail);
    }

    /// <summary>The combined action: ban layers first, then the policy — one helper run, one UAC.</summary>
    [Fact]
    public void ElevatedAllAppliesBanLayersThenPolicy()
    {
        var config = new AppConfig();
        config.Guard.HostsFile = Path.Combine(_tempDir, "hosts-all-elev");
        File.WriteAllText(config.Guard.HostsFile, "127.0.0.1 localhost\r\n");
        config.Guard.BanProbes = ["probe.invalid"];
        config.ForwardProxy.Listen = "127.0.0.1:18082";
        var ops = new FakeBanSystemOps();
        var policy = new FakeEgressPolicyStore();
        using var log = new FileLog(Path.Combine(_tempDir, "elev-all.log"));

        var onDetail = GuardElevatedCommands.ApplyAll(config, log, ops, policy, enable: true);

        Assert.Equal(["nrpt-on", "fw-on", "flush"], ops.Calls);
        Assert.Equal("http://127.0.0.1:18082", policy.AppliedUrl);
        Assert.False(policy.Removed);
        Assert.Contains("egressProxyUrl=http://127.0.0.1:18082", onDetail);

        var offDetail = GuardElevatedCommands.ApplyAll(config, log, ops, policy, enable: false);

        Assert.Equal(["nrpt-on", "fw-on", "flush", "nrpt-off", "fw-off", "flush"], ops.Calls);
        Assert.True(policy.Removed);
        Assert.Contains("removed", offDetail);
    }

    public void Dispose()
    {
        foreach (var log in _logs)
        {
            log.Dispose();
        }

        Directory.Delete(_tempDir, recursive: true);
    }
}
