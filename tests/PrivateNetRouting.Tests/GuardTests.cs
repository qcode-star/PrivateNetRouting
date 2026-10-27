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

    /// <summary>stdin of the reading probes (state read), by distro.</summary>
    public List<(string Distro, string Script)> Probes { get; } = [];

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
                Probes.Add((distro, standardInput));
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
    public void HostsBlockWithoutEndMarkerIsNeverRewritten()
    {
        var path = Path.Combine(_tempDir, "hosts");
        var original = "127.0.0.1 localhost\n# >>> ban\n0.0.0.0 old.example\n# docker section\n10.0.0.5 host.docker.internal\n";
        File.WriteAllText(path, original);
        var domains = new List<string> { "claude.ai" };

        Assert.Throws<IOException>(() => HostsFileBlock.Apply(path, "# >>> ban", "# <<< ban", "# comment", domains));
        Assert.Throws<IOException>(() => HostsFileBlock.Remove(path, "# >>> ban", "# <<< ban"));

        Assert.Equal(original, File.ReadAllText(path));
        var state = HostsFileBlock.Read(path, "# >>> ban", "# <<< ban", "# comment", domains);
        Assert.True(state.Present);
        Assert.False(state.Current);
        Assert.Equal(0, state.EntryCount);
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
        // WSL dials the upstream's own address: nothing listens on <gateway>:7080
        Assert.Equal("http://192.0.2.10:7080", ClaudeEnvValues.WslProxyUrl(IPAddress.Parse("192.0.2.1"), config));
        Assert.Equal("http://192.0.2.10:7080", ClaudeEnvValues.WslProxyUrl(null, config));
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

    [Theory]
    [InlineData("""{ "env": "http://old:1", "other": 1 }""")]
    [InlineData("""{ "env": null }""")]
    [InlineData("""{ "env": [1, 2] }""")]
    [InlineData("""[ { "env": {} } ]""")]
    public void SettingsSyncRefusesANonObjectRootOrEnvAndLeavesTheFileUntouched(string content)
    {
        // the distro boot script refuses such a file; the Windows side must not replace it either
        var config = SettingsConfig();
        File.WriteAllText(config.Claude.SettingsPath, content);
        var guard = new ClaudeSettingsGuard(config);

        Assert.Throws<InvalidDataException>(() => guard.Sync());

        Assert.Equal(content, File.ReadAllText(config.Claude.SettingsPath));
        Assert.Empty(Directory.GetFiles(_tempDir, "settings.json.pnr-bak-*"));
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

    [Fact]
    public void SettingsSyncMaintainsPrivacySwitchesAndNoCoAuthorKeys()
    {
        var config = SettingsConfig();
        File.WriteAllText(config.Claude.SettingsPath, """
            { "env": { "FOO": "bar" }, "model": "opus" }
            """);

        var guard = new ClaudeSettingsGuard(config);
        Assert.True(guard.Sync().Written);

        var json = JsonNode.Parse(File.ReadAllText(config.Claude.SettingsPath))!;
        var env = json["env"]!;
        Assert.Equal("bar", (string?)env["FOO"]); // foreign keys survive
        Assert.Equal("opus", (string?)json["model"]);
        Assert.Equal("1", (string?)env["DISABLE_TELEMETRY"]);
        Assert.Equal("1", (string?)env["DISABLE_ERROR_REPORTING"]);
        Assert.Equal("1", (string?)env["DISABLE_BUG_COMMAND"]);
        Assert.Equal("1", (string?)env["CLAUDE_CODE_DISABLE_FEEDBACK_SURVEY"]);
        Assert.Equal("1", (string?)env["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"]);
        Assert.False((bool)json["includeCoAuthoredBy"]!);
        Assert.Equal("", (string?)json["attribution"]!["commit"]);
        Assert.Equal("", (string?)json["attribution"]!["pr"]);
        Assert.Equal(GuardState.On, guard.Read().State);

        // the second sync finds the file already in the maintained shape: no rewrite
        Assert.False(guard.Sync().Written);
    }

    [Fact]
    public void SettingsSyncFillsAttributionAndLeavesAnUnmaintainedShapeAsWritten()
    {
        var config = SettingsConfig();
        var path = config.Claude.SettingsPath;
        var guard = new ClaudeSettingsGuard(config);
        guard.Sync(); // the full maintained shape

        // attribution: false hides everything — nothing to add
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        root["attribution"] = false;
        File.WriteAllText(path, root.ToJsonString());
        Assert.False(guard.Sync().Written);
        Assert.False((bool)JsonNode.Parse(File.ReadAllText(path))!["attribution"]!);

        // a foreign sub-key survives; the hidden commit and PR parts are filled
        root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        root["attribution"] = new JsonObject { ["sessionUrl"] = true };
        File.WriteAllText(path, root.ToJsonString());
        Assert.True(guard.Sync().Written);
        var attribution = JsonNode.Parse(File.ReadAllText(path))!["attribution"]!;
        Assert.Equal("", (string?)attribution["commit"]);
        Assert.Equal("", (string?)attribution["pr"]);
        Assert.True((bool)attribution["sessionUrl"]!);

        // an attribution shape we do not maintain is left as written and reported as drift
        root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        root["attribution"] = 42;
        File.WriteAllText(path, root.ToJsonString());
        Assert.False(guard.Sync().Written);
        Assert.Equal(42, (int)JsonNode.Parse(File.ReadAllText(path))!["attribution"]!);
        Assert.Contains("attribution", guard.Read().Drift!);

        // includeCoAuthoredBy is maintained off even when the file says on
        root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        root["includeCoAuthoredBy"] = true;
        File.WriteAllText(path, root.ToJsonString());
        Assert.True(guard.Sync().Written);
        Assert.False((bool)JsonNode.Parse(File.ReadAllText(path))!["includeCoAuthoredBy"]!);
    }

    [Fact]
    public void SettingsStatusReportsUnsetKeysByName()
    {
        var config = SettingsConfig();
        var guard = new ClaudeSettingsGuard(config);
        guard.Sync();

        // the proxy env is correct, but the maintained keys are missing or wrong
        File.WriteAllText(config.Claude.SettingsPath, """
            {
              "env": {
                "HTTPS_PROXY": "http://127.0.0.1:18082",
                "HTTP_PROXY": "http://127.0.0.1:18082",
                "NO_PROXY": "localhost,127.0.0.1,.local,corp.example.com,.corp.example.com",
                "DISABLE_TELEMETRY": true
              },
              "includeCoAuthoredBy": true
            }
            """);

        var status = guard.Read();

        Assert.Equal(GuardState.Partial, status.State);
        Assert.Equal("http://127.0.0.1:18082", status.CurrentProxy); // the env part is fine
        Assert.Contains("DISABLE_TELEMETRY", status.Drift!); // a wrong value counts as not set
        Assert.Contains("CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC", status.Drift!);
        Assert.Contains("includeCoAuthoredBy", status.Drift!);
        Assert.Contains("attribution", status.Drift!);
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

    [Theory]
    // a trailing comment on the table header
    [InlineData("[features] # tweaks\nother = 1\n", "[features] # tweaks\nrespect_system_proxy = true\nother = 1\n")]
    [InlineData("[features]# tweaks\nrespect_system_proxy = false\n", "[features]# tweaks\nrespect_system_proxy = true\n")]
    // the table as an inline table
    [InlineData("features = { other = 1 }\n", "features = { respect_system_proxy = true, other = 1 }\n")]
    [InlineData("features = {}\n", "features = { respect_system_proxy = true }\n")]
    [InlineData("features = { respect_system_proxy = false, other = 1 }\n", "features = { respect_system_proxy = true, other = 1 }\n")]
    // the table defined by top-level dotted keys
    [InlineData("features.other = true\n[desktop]\nx = 1\n", "features.other = true\nfeatures.respect_system_proxy = true\n[desktop]\nx = 1\n")]
    // a multi-line value of the dotted sibling is not split: the flag goes after its last line
    [InlineData("features.list = [\n  \"a\",\n  \"b]\",\n]\n\n[desktop]\nx = 1\n", "features.list = [\n  \"a\",\n  \"b]\",\n]\nfeatures.respect_system_proxy = true\n\n[desktop]\nx = 1\n")]
    [InlineData("features.nested = [[1, 2],\n  [3]] # done\nother = 2\n", "features.nested = [[1, 2],\n  [3]] # done\nfeatures.respect_system_proxy = true\nother = 2\n")]
    [InlineData("features.text = \"\"\"\nline [\n\"\"\"\n", "features.text = \"\"\"\nline [\n\"\"\"\nfeatures.respect_system_proxy = true\n")]
    // a key-like line inside another key's multi-line array is not a sibling
    [InlineData("x = [\n  \"features.y = 1\",\n]\n", "x = [\n  \"features.y = 1\",\n]\n\n[features]\nrespect_system_proxy = true\n")]
    // a longer key ending in the flag's name is not the flag
    [InlineData("features = { no_respect_system_proxy = false }\n", "features = { respect_system_proxy = true, no_respect_system_proxy = false }\n")]
    [InlineData("features = { a.respect_system_proxy = false }\n", "features = { respect_system_proxy = true, a.respect_system_proxy = false }\n")]
    [InlineData("features = { \"respect_system_proxy\" = false }\n", "features = { respect_system_proxy = true }\n")]
    // a """ inside a comment opens no string, so the real header after it is still seen
    [InlineData("# a \"\"\" b\n[features]\nother = 1\n", "# a \"\"\" b\n[features]\nrespect_system_proxy = true\nother = 1\n")]
    public void CodexSettingsApplyRecognizesEveryFormOfTheFeaturesTable(string existing, string expected)
    {
        var applied = CodexSettingsGuard.Apply(existing);

        Assert.Equal(expected, applied);
        Assert.Equal(true, CodexSettingsGuard.FindValue(applied));
        Assert.Equal(applied, CodexSettingsGuard.Apply(applied)); // idempotent
    }

    [Fact]
    public void CodexSettingsReadSeesTheFlagInsideAnInlineTable()
    {
        Assert.Equal(false, CodexSettingsGuard.FindValue("features = { respect_system_proxy = false }\n"));
        Assert.Null(CodexSettingsGuard.FindValue("features = { other = 1 }\n"));
        Assert.Null(CodexSettingsGuard.FindValue("features = { no_respect_system_proxy = false }\n"));
        Assert.Null(CodexSettingsGuard.FindValue("features = { note = \"respect_system_proxy = true\" }\n"));
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
        Assert.StartsWith("$ErrorActionPreference = 'Stop'", script); // a failing statement must not be masked by a succeeding last one
        Assert.Contains("Remove-DnsClientNrptRule", script); // idempotence: remove first
        Assert.Contains("-Namespace @('claude.ai','.claude.ai','openai.com','.openai.com')", script);
        Assert.Contains("-NameServers @('127.0.0.1')", script);
        Assert.Contains("-DisplayName 'ai''direct'", script);
        var remove = ops.BuildNrptScript(apply: false);
        Assert.StartsWith("$ErrorActionPreference = 'Stop'", remove);
        Assert.DoesNotContain("Add-DnsClientNrptRule", remove);
    }

    [Fact]
    public void FirewallScriptSplitsFamiliesAndUsesGroup()
    {
        var config = new AppConfig();
        config.Claude.FirewallSubnets = ["160.79.104.0/21", "2607:6bc0::/48", "2607:6bc0:11::/48"];
        var ops = new PowerShellBanSystemOps(config, new StubPowerShell());

        var script = ops.BuildFirewallScript(apply: true);
        Assert.StartsWith("$ErrorActionPreference = 'Stop'", script); // a failing IPv4 rule must not be masked by a succeeding IPv6 one
        Assert.Contains("Remove-NetFirewallRule", script);
        Assert.Contains("-DisplayName 'ai-direct-ban: IPv4' -Group 'ai-direct-ban'", script);
        Assert.Contains("-RemoteAddress @('160.79.104.0/21')", script);
        Assert.Contains("-DisplayName 'ai-direct-ban: IPv6'", script);
        Assert.Contains("-RemoteAddress @('2607:6bc0::/48','2607:6bc0:11::/48')", script);
        Assert.StartsWith("$ErrorActionPreference = 'Stop'", ops.BuildFirewallScript(apply: false));
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

    /// <summary>The settings.json body of a distro in sync: the proxy env plus the maintained privacy/attribution keys.</summary>
    private static string FullSettingsJson(string proxy)
    {
        var root = new JsonObject();
        ClaudeSettingsShape.Apply(root, proxy, "localhost,127.0.0.1,.local");
        return root.ToJsonString();
    }

    /// <summary>Canned probe answer of a distro whose layers match the config (port 18082).</summary>
    private static string WslProbeInSync(string user, string proxy = "http://192.0.2.1:18082") =>
        $"SCRIPT=present\nPORT=18082\nUSER_NAME='{user}'\n"
        + "PROXYENV=present\nPNR_CODEX_PROXY_PORT=18082\n"
        + $"SETTINGS=present\n{FullSettingsJson(proxy)}\nSETTINGS=END\n";

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

    /// <summary>A WSL config in a skipped shape: the http mode, the applications endpoint is the upstream itself.</summary>
    private AppConfig WslSkippedShapeConfig(string upstream)
    {
        var config = WslConfig("on", ("d1", "u1"));
        config.Chain.Mode = "http";
        config.Chain.Http.Upstream = upstream;
        return config;
    }

    [Fact]
    public async Task WslSkippedShapeSyncsTheUpstreamAddressNotTheGateway()
    {
        var runner = new FakeWslRunner { Installed = ["d1"], Running = ["d1"] };
        var guard = new WslConfigGuard(
            WslSkippedShapeConfig("192.0.2.50:3128"),
            runner,
            gatewayProvider: () => IPAddress.Parse("192.0.2.1"));

        var result = await guard.SyncAsync(CancellationToken.None);

        var d1 = Assert.Single(result.Distros);
        Assert.Equal("http://192.0.2.50:3128", d1.ProxyUrl); // not http://<gateway>:3128
        var startup = Assert.Single(runner.Scripts, s => s.Script.Contains("10-claude-proxy.sh"));
        var script = Encoding.UTF8.GetString(Convert.FromBase64String(
            ExtractBase64Payload(startup.Script, "SCRIPT='/etc/wsl.d/startup/10-claude-proxy.sh'")));
        Assert.Contains("PORT=3128", script);
        Assert.Contains("PNR_PROXY_HOST='192.0.2.50'", script);
        var envPass = Assert.Single(runner.Scripts, s => s.Script.Contains("ENVFILE="));
        var env = Encoding.UTF8.GetString(Convert.FromBase64String(
            ExtractBase64Payload(envPass.Script, "ENVFILE='/etc/profile.d/20-pnr-codex-proxy.sh'")));
        Assert.Contains("PNR_CODEX_PROXY_PORT=3128", env);
        Assert.Contains("PNR_CODEX_PROXY_HOST='192.0.2.50'", env);
        var settings = Assert.Single(runner.Scripts, s => s.User == "u1");
        var settingsJson = Encoding.UTF8.GetString(Convert.FromBase64String(
            ExtractBase64Payload(settings.Script, "SETTINGS=\"$HOME/.claude/settings.json\"")));
        Assert.Contains("\"HTTPS_PROXY\": \"http://192.0.2.50:3128\"", settingsJson);
    }

    [Fact]
    public async Task WslSkippedShapeBracketsAnIpv6UpstreamInEveryUrl()
    {
        var runner = new FakeWslRunner { Installed = ["d1"], Running = ["d1"] };
        var config = WslSkippedShapeConfig("[2001:db8::1]:1080");
        var guard = new WslConfigGuard(config, runner, gatewayProvider: () => IPAddress.Parse("192.0.2.1"));

        Assert.Equal("2001:db8::1", ClaudeEnvValues.WslFixedHost(config)); // bare: the status compares it
        Assert.Equal("http://[2001:db8::1]:1080", ClaudeEnvValues.WslProxyUrl(null, config));
        var result = await guard.SyncAsync(CancellationToken.None);

        Assert.Equal("http://[2001:db8::1]:1080", Assert.Single(result.Distros).ProxyUrl);
        var settings = Assert.Single(runner.Scripts, s => s.User == "u1");
        var settingsJson = Encoding.UTF8.GetString(Convert.FromBase64String(
            ExtractBase64Payload(settings.Script, "SETTINGS=\"$HOME/.claude/settings.json\"")));
        Assert.Contains("\"HTTPS_PROXY\": \"http://[2001:db8::1]:1080\"", settingsJson);

        // the scripts bake the bare address (bash /dev/tcp takes it so) and bracket it in the URL
        var startup = Assert.Single(runner.Scripts, s => s.Script.Contains("10-claude-proxy.sh"));
        var script = Encoding.UTF8.GetString(Convert.FromBase64String(
            ExtractBase64Payload(startup.Script, "SCRIPT='/etc/wsl.d/startup/10-claude-proxy.sh'")));
        Assert.Contains("PNR_PROXY_HOST='2001:db8::1'", script);
        Assert.Contains("case \"$HOST_IP\" in *:*) URL_HOST=\"[$HOST_IP]\" ;; esac", script);
        Assert.Contains("PROXY=\"http://$URL_HOST:$PORT\"", script);
        Assert.Contains("</dev/tcp/$HOST_IP/$PORT", script);
        var envPass = Assert.Single(runner.Scripts, s => s.Script.Contains("ENVFILE="));
        var env = Encoding.UTF8.GetString(Convert.FromBase64String(
            ExtractBase64Payload(envPass.Script, "ENVFILE='/etc/profile.d/20-pnr-codex-proxy.sh'")));
        Assert.Contains("PNR_CODEX_PROXY_HOST='2001:db8::1'", env);
        Assert.Contains("case \"$_pnr_host_ip\" in *:*) _pnr_url_host=\"[$_pnr_host_ip]\" ;; esac", env);
        // brackets are a character class to a grep regex: the .env check is a fixed-string match
        Assert.Contains("grep -qxF \"HTTP_PROXY=$HTTP_PROXY\"", env);
        Assert.DoesNotContain('\r', script);
        Assert.DoesNotContain('\r', env);

        // the status reads the bare address back and finds the distro in sync
        runner.ProbeOutput = _ => "SCRIPT=present\nPORT=1080\nPNR_PROXY_HOST='2001:db8::1'\nUSER_NAME='u1'\n"
            + "PROXYENV=present\nPNR_CODEX_PROXY_PORT=1080\nPNR_CODEX_PROXY_HOST='2001:db8::1'\n"
            + $"SETTINGS=present\n{FullSettingsJson("http://[2001:db8::1]:1080")}\nSETTINGS=END\n";
        var status = await guard.ReadAsync(CancellationToken.None);
        Assert.Equal(GuardState.On, status.Distros[0].State);
    }

    [Fact]
    public async Task WslLocalShapeKeepsTheGatewayAndBakesNoHost()
    {
        var runner = new FakeWslRunner { Installed = ["d1"], Running = ["d1"] };
        var guard = new WslConfigGuard(
            WslConfig("on", ("d1", "u1")),
            runner,
            gatewayProvider: () => IPAddress.Parse("192.0.2.1"));

        await guard.SyncAsync(CancellationToken.None);

        var startup = Assert.Single(runner.Scripts, s => s.Script.Contains("10-claude-proxy.sh"));
        var script = Encoding.UTF8.GetString(Convert.FromBase64String(
            ExtractBase64Payload(startup.Script, "SCRIPT='/etc/wsl.d/startup/10-claude-proxy.sh'")));
        Assert.DoesNotContain("PNR_PROXY_HOST='", script);
        Assert.Contains("ip route show default", script);
    }

    [Fact]
    public void WslSkippedLoopbackUpstreamStillUsesTheGateway()
    {
        // a loopback upstream lives on the Windows host: the distros reach it through the gateway
        // (the status warns separately that a loopback-only listener is unreachable from WSL)
        Assert.Null(ClaudeEnvValues.WslFixedHost(WslSkippedShapeConfig("127.0.0.1:3128")));
        Assert.Equal("http://192.0.2.1:3128", ClaudeEnvValues.WslProxyUrl(IPAddress.Parse("192.0.2.1"), WslSkippedShapeConfig("127.0.0.1:3128")));
    }

    [Fact]
    public async Task WslSkippedShapeStatusComparesTheUpstreamHost()
    {
        var config = WslSkippedShapeConfig("192.0.2.50:3128");
        var inSync = "SCRIPT=present\nPORT=3128\nPNR_PROXY_HOST='192.0.2.50'\nUSER_NAME='u1'\n"
            + "PROXYENV=present\nPNR_CODEX_PROXY_PORT=3128\nPNR_CODEX_PROXY_HOST='192.0.2.50'\n"
            + $"SETTINGS=present\n{FullSettingsJson("http://192.0.2.50:3128")}\nSETTINGS=END\n";
        // what the gateway-form scripts of an earlier sync look like: right port, no baked host
        var gatewayForm = "SCRIPT=present\nPORT=3128\nUSER_NAME='u1'\n"
            + "PROXYENV=present\nPNR_CODEX_PROXY_PORT=3128\n"
            + $"SETTINGS=present\n{FullSettingsJson("http://192.0.2.1:3128")}\nSETTINGS=END\n";
        var probe = inSync;
        var runner = new FakeWslRunner { Installed = ["d1"], Running = ["d1"], ProbeOutput = _ => probe };
        var guard = new WslConfigGuard(config, runner, gatewayProvider: () => IPAddress.Parse("192.0.2.1"));

        var ok = await guard.ReadAsync(CancellationToken.None);
        Assert.Equal(GuardState.On, ok.Distros[0].State);
        Assert.Equal("http://192.0.2.50:3128", ok.ExpectedProxy);

        probe = gatewayForm;
        var stale = await guard.ReadAsync(CancellationToken.None);
        Assert.Equal(GuardState.Partial, stale.Distros[0].State);
        Assert.Contains("host", stale.Distros[0].Reason);
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
                  + $"SETTINGS=present\n{FullSettingsJson("http://192.0.2.1:18082")}\nSETTINGS=END\n",
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
                + $"SETTINGS=present\n{FullSettingsJson("http://192.0.2.1:18082")}\nSETTINGS=END\n",
        };
        var config = WslConfig("on", syncClaude: true, syncCodex: false, ("d1", "u1"));
        var guard = new WslConfigGuard(config, runner, gatewayProvider: () => IPAddress.Parse("192.0.2.1"));

        var status = await guard.ReadAsync(CancellationToken.None);

        Assert.Equal(GuardState.On, status.Distros[0].State);
    }

    [Fact]
    public async Task WslSettingsSyncCarriesThePrivacyAndAttributionKeys()
    {
        var runner = new FakeWslRunner { Installed = ["d1"], Running = ["d1"] };
        var guard = new WslConfigGuard(
            WslConfig("on", ("d1", "u1")),
            runner,
            gatewayProvider: () => IPAddress.Parse("192.0.2.1"));

        await guard.SyncAsync(CancellationToken.None);

        var settings = Assert.Single(runner.Scripts, s => s.User == "u1");
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(
            ExtractBase64Payload(settings.Script, "SETTINGS=\"$HOME/.claude/settings.json\"")));
        var root = JsonNode.Parse(json)!;
        Assert.Equal("1", (string?)root["env"]!["DISABLE_TELEMETRY"]);
        Assert.Equal("1", (string?)root["env"]!["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"]);
        Assert.False((bool)root["includeCoAuthoredBy"]!);
        Assert.Equal("", (string?)root["attribution"]!["commit"]);
        Assert.Equal("", (string?)root["attribution"]!["pr"]);
    }

    [Fact]
    public async Task WslStatusReportsSettingsKeysNotSet()
    {
        // the proxy env is right but the maintained privacy/attribution keys are missing
        var probe = "SCRIPT=present\nPORT=18082\nUSER_NAME='u1'\nPROXYENV=present\nPNR_CODEX_PROXY_PORT=18082\n"
            + "SETTINGS=present\n{\"env\":{\"HTTPS_PROXY\":\"http://192.0.2.1:18082\"},\"includeCoAuthoredBy\":true}\nSETTINGS=END\n";
        var runner = new FakeWslRunner { Installed = ["d1"], Running = ["d1"], ProbeOutput = _ => probe };
        var guard = new WslConfigGuard(
            WslConfig("on", ("d1", "u1")),
            runner,
            gatewayProvider: () => IPAddress.Parse("192.0.2.1"));

        var status = await guard.ReadAsync(CancellationToken.None);

        Assert.Equal(GuardState.Partial, status.Distros[0].State);
        Assert.Contains("keys not set", status.Distros[0].Reason);
        Assert.Contains("DISABLE_TELEMETRY", status.Distros[0].Reason);
        Assert.Contains("attribution", status.Distros[0].Reason);
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
        Assert.Contains("export HTTP_PROXY=\"http://$_pnr_url_host:$PNR_CODEX_PROXY_PORT\"", script);
        Assert.Contains("export NO_PROXY=\"localhost,127.0.0.1,.local,corp.example.com,.corp.example.com\"", script);
        // the port line is what `pnr guard status` reads back
        Assert.Contains("PNR_CODEX_PROXY_PORT=18082", script);
        Assert.DoesNotContain("127.0.0.1:18082", script);
        // the .env of CODEX_HOME is rewritten to the gateway address: the Codex core loads it over
        // the inherited environment, so the exports alone would be overridden by the loopback form
        Assert.Contains("_pnr_env=\"$CODEX_HOME/.env\"", script);
        Assert.Contains("grep -qxF \"HTTP_PROXY=$HTTP_PROXY\"", script);
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
        var payload = ExtractBase64Payload(root.Script, "SCRIPT='/etc/wsl.d/startup/10-claude-proxy.sh'");
        var script = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
        Assert.Contains("USER_NAME='u1'", script);
        Assert.Contains("PORT=18082", script);
        Assert.Contains("env['HTTPS_PROXY'] = proxy", script);
        Assert.DoesNotContain("env['NO_PROXY']", script); // the startup script does not touch NO_PROXY (mentioned only in a comment)

        // the boot-time writer must never wipe an unparseable settings.json: it reads with a
        // BOM-tolerant codec, refuses on a parse error (non-zero exit, file untouched) and
        // replaces the file through a temp file
        Assert.Contains("encoding='utf-8-sig'", script);
        Assert.Contains("sys.exit(3)", script);
        Assert.Contains("os.replace(tmp, real)", script);
        Assert.DoesNotContain("data = {}\n            except", script);

        // the script runs as root: ~/.claude is created for the user with the primary group
        // looked up, not assumed to be named like the user
        Assert.Contains("USER_GROUP=$(id -gn \"$USER_NAME\"", script);
        Assert.Contains("install -d -o \"$USER_NAME\" -g \"$USER_GROUP\"", script);
        Assert.Contains("chown \"$USER_NAME:$USER_GROUP\" \"$SETTINGS\"", script);
        Assert.DoesNotContain("mkdir -p \"$USER_HOME/.claude\"", script);

        // codex proxy env contents: the marker, the port and the bypass zones of the config
        var envPass = Assert.Single(runner.Scripts, s => s.Distro == "d1" && s.User == "root" && s.Script.Contains("ENVFILE="));
        Assert.Contains("pnr_bak \"$ENVFILE\" BACKUP", envPass.Script);
        var envPayload = ExtractBase64Payload(envPass.Script, "ENVFILE='/etc/profile.d/20-pnr-codex-proxy.sh'");
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
    public async Task WslSyncReportsADistroWhoseScriptDidNotRunAndKeepsGoing()
    {
        // a bash syntax error inside the distro does not change the wsl.exe exit code
        var runner = new FakeWslRunner
        {
            Installed = ["d1", "d2"],
            Running = ["d1", "d2"],
            WriteOutput = "bash: line 3: syntax error near unexpected token\n",
        };
        var guard = new WslConfigGuard(
            WslConfig("on", ("d1", "u1"), ("d2", "u2")),
            runner,
            gatewayProvider: () => IPAddress.Parse("192.0.2.1"));

        var result = await guard.SyncAsync(CancellationToken.None);

        // the failure is the distro's own result, and the next distro is still attempted
        Assert.Equal(2, result.Distros.Count);
        Assert.All(result.Distros, d => Assert.True(d.Failed));
        Assert.Contains("did not run to the end", result.Distros[0].Reason);
        Assert.Contains("syntax error", result.Distros[0].Reason);
        Assert.Contains(runner.Scripts, s => s.Distro == "d2");
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

        // a command that only references the runner (a wrapper, a background or redirected
        // form) already runs it: adopting it as 00-user-boot-command.sh would make the runner
        // call itself at every boot
        foreach (var command in new[]
        {
            $"bash {runnerPath}",
            $"sh {runnerPath}",
            $"{runnerPath} &",
            $"{runnerPath} >/dev/null 2>&1",
            $"\"{runnerPath}\"",
            $"docker start things; {runnerPath}",
        })
        {
            Assert.Equal(WslBootPlan.AlreadyOurs, WslConfigGuard.PlanBootLayer(runnerPath, command, WslBootRunnerState.Ours));
            Assert.Equal(WslBootPlan.TakeoverRunner, WslConfigGuard.PlanBootLayer(runnerPath, command, WslBootRunnerState.Foreign));
            Assert.Equal(WslBootPlan.TakeoverRunner, WslConfigGuard.PlanBootLayer(runnerPath, command, WslBootRunnerState.Missing));
        }

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

    [Theory]
    [InlineData(@"C:\Users\u\AppData\Local\PrivateNetRouting\sidecar\elevated-guard-ban-on=0a1b2c3d.result", true)]
    [InlineData(@"C:\x\elevated-start-daemon=ffffffff.result", true)]
    [InlineData(@"C:\Windows\System32\drivers\etc\hosts", false)]
    [InlineData(@"C:\x\elevated-guard-ban-on=0a1b2c3d.txt", false)]
    [InlineData(@"C:\x\result", false)]
    [InlineData(@"C:\x\=0a1b2c3d.result", false)]
    [InlineData(@"..\elevated-guard-ban-on=0a1b2c3d.result", false)]
    [InlineData("", false)]
    public void ElevatedHelperWritesOnlyTheResultFileShapeItCreates(string path, bool expected)
        => Assert.Equal(expected, ElevatedHelperTrust.IsValidResultFile(path));

    [Fact]
    public void ElevatedGuardHelperRefusesAForeignResultFileWithoutTouchingIt()
    {
        var target = Path.Combine(Path.GetTempPath(), $"pnr-victim-{Guid.NewGuid():N}.txt");
        File.WriteAllText(target, "precious");
        try
        {
            var code = GuardElevatedCommands.Execute(GuardElevatedCommands.BanOn, target);

            Assert.Equal(1, code);
            Assert.Equal("precious", File.ReadAllText(target));
        }
        finally
        {
            File.Delete(target);
        }
    }

    [Fact]
    public void ElevatedHelperPinsPowerShellAndHostsToTheSystem()
    {
        var config = new AppConfig { PowerShellPath = @"C:\Users\u\evil\powershell.exe" };
        config.Guard.HostsFile = @"C:\Users\u\anywhere\hosts";
        var logPath = Path.Combine(Path.GetTempPath(), $"pnr-pin-{Guid.NewGuid():N}.log");
        using (var log = new FileLog(logPath))
        {
            GuardElevatedCommands.PinSystemLocations(config, log);
        }

        File.Delete(logPath);
        Assert.Equal(ElevatedHelperTrust.SystemPowerShell(), config.PowerShellPath);
        Assert.Equal(ElevatedHelperTrust.SystemHostsFile(), config.Guard.HostsFile);
        Assert.EndsWith(@"WindowsPowerShell\v1.0\powershell.exe", config.PowerShellPath, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(@"drivers\etc\hosts", config.Guard.HostsFile, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The scripts end with PNR=ok only when every write was verified: a failed decode, an empty
    /// payload, a failed backup or a failed replacement stops them with a FAILED line instead of
    /// truncating a settings file and reporting success.
    /// </summary>
    // ----- live ban verdicts -----

    [Fact]
    public async Task LiveChecksWithoutProbeNamesAreNotChecked()
    {
        var config = new AppConfig();
        config.Guard.BanProbes = [];
        var service = MakeService(config, new FakeEgressPolicyStore(), new FakeBanSystemOps(), new FakeSystemProxyStore(), new FakeElevatedRunner(), out _);

        var live = await service.LiveChecksAsync(CancellationToken.None);

        Assert.Null(live.DnsBlocked); // not a vacuous "blocked"
        Assert.Null(live.DirectBlocked);
        Assert.Contains("not checked", live.DnsDetail);
    }

    [Fact]
    public void ProbeVerdictsCombineAnyOpenProbeMakesItOpen()
    {
        Assert.True(GuardService.CombineVerdicts([true, true]));
        Assert.False(GuardService.CombineVerdicts([true, false]));
        Assert.Null(GuardService.CombineVerdicts([true, null]));
        Assert.False(GuardService.CombineVerdicts([null, false]));
        Assert.Null(GuardService.CombineVerdicts([]));
    }

    // ----- the collapsed Guards card: summary state and "all on" -----

    private static GuardSnapshot Snapshot(
        GuardState ban, GuardState policy, GuardState claude, GuardState codex, WslConfigStatus wsl) => new(
        new LocalBanStatus(ban, 0, true, 0, [], 0, []),
        new EgressPolicyStatus(policy, null, "", null),
        new ClaudeSettingsStatus(claude, null, null, "", "", false),
        new CodexSettingsStatus(codex, null, false, true),
        wsl,
        new SystemProxyStatus(false, null, null),
        []);

    private static WslDistroStatus Distro(GuardState state, bool inactive = false) =>
        new("d", true, state, null, null, "", Inactive: inactive);

    [Fact]
    public void GuardsCardReachesAllOnWithTheWslLayerOffByConfig()
    {
        // the default config has no WSL: its "off" must not keep the card from "all on"
        var snapshot = Snapshot(GuardState.On, GuardState.On, GuardState.On, GuardState.On, WslConfigStatus.LayerOff("no distros configured"));

        Assert.Null(GuardAggregate.WslLayer(snapshot.Wsl));
        Assert.True(GuardAggregate.AllOn(snapshot));
        Assert.Equal(GuardState.On, GuardAggregate.Summary(snapshot));
    }

    [Fact]
    public void AStoppedOrAbsentDistroDoesNotVote()
    {
        var wsl = new WslConfigStatus(GuardState.Unknown, [Distro(GuardState.Unknown, inactive: true)], 18081, null, "");
        var snapshot = Snapshot(GuardState.On, GuardState.On, GuardState.On, GuardState.On, wsl);

        Assert.True(GuardAggregate.AllOn(snapshot));
        Assert.Equal(GuardState.On, GuardAggregate.Summary(snapshot));
    }

    [Fact]
    public void ARunningDistroThatIsNotInSyncKeepsTheCardFromAllOn()
    {
        var wsl = new WslConfigStatus(GuardState.Partial, [Distro(GuardState.On), Distro(GuardState.Partial)], 18081, null, "");
        var snapshot = Snapshot(GuardState.On, GuardState.On, GuardState.On, GuardState.On, wsl);

        Assert.False(GuardAggregate.AllOn(snapshot));
        Assert.Equal(GuardState.Partial, GuardAggregate.Summary(snapshot));
    }

    [Fact]
    public void SummaryFoldsLayersUnknownDominatesAndMixesArePartial()
    {
        var off = WslConfigStatus.LayerOff("off");

        Assert.Equal(GuardState.Off, GuardAggregate.Summary(Snapshot(GuardState.Off, GuardState.Off, GuardState.Off, GuardState.Off, off)));
        Assert.Equal(GuardState.Partial, GuardAggregate.Summary(Snapshot(GuardState.On, GuardState.Off, GuardState.On, GuardState.On, off)));
        Assert.Equal(GuardState.Unknown, GuardAggregate.Summary(Snapshot(GuardState.On, GuardState.Off, GuardState.Unknown, GuardState.On, off)));
        // Claude Code and Codex settings are one layer: one on and one off is a mix, not "all on"
        Assert.False(GuardAggregate.AllOn(Snapshot(GuardState.On, GuardState.On, GuardState.On, GuardState.Off, off)));
    }

    [Fact]
    public async Task WslWriteScriptsVerifyEveryWriteAndNeverOverwriteAfterAFailedBackup()
    {
        var runner = new FakeWslRunner
        {
            Installed = ["d1"],
            Running = ["d1"],
            ProbeOutput = _ => "SCRIPT=missing\nPROXYENV=missing\nRUNNER=missing\nWSLCONF=missing\nSETTINGS=missing\n",
            WriteOutput = "RUNNER=written\nWSLCONF=written\nSETTINGS=written\nSCRIPT=written\nENVFILE=written\nPNR=ok\n",
        };
        var guard = new WslConfigGuard(WslConfig("on", ("d1", "u1")), runner, gatewayProvider: () => IPAddress.Parse("192.0.2.1"));

        await guard.SyncAsync(CancellationToken.None);

        Assert.Equal(5, runner.Scripts.Count);
        foreach (var (_, _, script) in runner.Scripts.Where(s => !s.Script.Contains("CLAUDEDIR=chowned")))
        {
            Assert.Contains("pnr_put()", script);
            Assert.DoesNotContain("cat \"$NEW\" >", script); // no unverified truncate-and-write
            Assert.All(
                script.Split('\n').Where(l => l.Contains("base64 -d")),
                l => Assert.Contains("|| { echo \"FAILED: cannot write the payload to /tmp\"; exit 1; }", l));
            Assert.All(
                script.Split('\n').Where(l => l.Contains("then pnr_bak ")),
                l => Assert.Contains("|| { echo \"FAILED: backup of", l));
            Assert.Equal(
                script.Split('\n').Count(l => l.Trim().StartsWith("pnr_put \"$NEW\"", StringComparison.Ordinal)),
                script.Split('\n').Count(l => l.Contains("pnr_put \"$NEW\"") && l.Contains("|| exit 1")));
        }

        // the user's settings directory is created by the script itself
        Assert.Contains(runner.Scripts, s => s.Script.Contains("mkdir -p \"$HOME/.claude\" ||"));
    }

    [Fact]
    public async Task WslSyncHandsARootOwnedClaudeDirectoryBackBeforeTheSettingsWrite()
    {
        var runner = new FakeWslRunner
        {
            Installed = ["d1"],
            Running = ["d1"],
            ProbeOutput = _ => "SCRIPT=missing\nPROXYENV=missing\nRUNNER=missing\nWSLCONF=missing\nSETTINGS=missing\n",
            WriteOutput = "CLAUDEDIR=chowned\nSETTINGS=written\nSCRIPT=written\nPNR=ok\n",
        };
        var guard = new WslConfigGuard(WslConfig("on", ("d1", "u1")), runner, gatewayProvider: () => IPAddress.Parse("192.0.2.1"));

        var result = await guard.SyncAsync(CancellationToken.None);

        // the root step runs right before the user-side settings write
        var owner = runner.Scripts.FindIndex(s => s.Script.Contains("CLAUDEDIR=chowned"));
        var settings = runner.Scripts.FindIndex(s => s.Script.Contains("SETTINGS=\"$HOME/.claude/settings.json\""));
        Assert.True(owner >= 0 && settings == owner + 1);
        Assert.Equal("root", runner.Scripts[owner].User);
        Assert.Contains("USER_NAME='u1'", runner.Scripts[owner].Script);
        Assert.True(result.Distros.Single().ClaudeDirChowned);
    }

    [Fact]
    public void ClaudeDirectoryRepairTouchesOnlyARootOwnedDirectoryOfAnOrdinaryUser()
    {
        var script = WslConfigGuard.BuildClaudeDirOwnerScript("o'neil");
        Assert.DoesNotContain('\r', script);
        Assert.Contains("USER_NAME='o'\\''neil'", script);
        Assert.Contains("[ \"$USER_NAME\" != \"root\" ]", script);
        Assert.Contains("[ ! -L \"$USER_HOME/.claude\" ]", script);
        Assert.Contains("[ \"$(stat -c %u \"$USER_HOME/.claude\")\" = \"0\" ]", script);
        Assert.DoesNotContain("chown -R", script); // the directory only, its files keep their owners
        Assert.Contains("exit 1", script); // a failed chown fails the sync step
        Assert.EndsWith("echo PNR=ok", script.TrimEnd());

        // the boot-time startup script repairs too, but a failed chown must not stop its proxy write
        var startup = WslConfigGuard.BuildStartupScript("u1", 18081);
        var repair = startup[startup.IndexOf("stat -c %u", StringComparison.Ordinal)..startup.IndexOf("python3 - ", StringComparison.Ordinal)];
        Assert.Contains("echo CLAUDEDIR=chowned", repair);
        Assert.DoesNotContain("exit 1", repair);
    }

    [Fact]
    public void WslPutFallsBackToAVerifiedInPlaceWriteInANonWritableDirectory()
    {
        // an older boot script created ~/.claude as root: the user can write the file but cannot
        // create the temp file next to it — the write goes in place and is compared afterwards
        var put = WslConfigGuard.PnrBakFn[WslConfigGuard.PnrBakFn.IndexOf("pnr_put()", StringComparison.Ordinal)..];
        Assert.Contains("[ -f \"$dest\" ] && [ -w \"$dest\" ] && [ ! -w \"$(dirname \"$dest\")\" ]", put);
        Assert.Contains("if cat \"$new\" > \"$dest\" && cmp -s \"$new\" \"$dest\"; then", put);
        Assert.Contains("FAILED: in-place write of $dest", put);
        // the backup reuses a writable slot file there instead of creating one, or fails honestly
        var bak = WslConfigGuard.PnrBakFn[..WslConfigGuard.PnrBakFn.IndexOf("pnr_put()", StringComparison.Ordinal)];
        Assert.Contains("if [ -w \"$(dirname \"$f\")\" ]; then", bak);
        Assert.Contains("FAILED: no backup slot for $f", bak);
        Assert.DoesNotContain('\r', WslConfigGuard.NormalizeLf(WslConfigGuard.PnrBakFn));
    }

    [Fact]
    public async Task WslProbeResolvesTheUsersRealHome()
    {
        var runner = new FakeWslRunner { Installed = ["d1"], Running = ["d1"], ProbeOutput = _ => WslProbeInSync("root") };
        var guard = new WslConfigGuard(WslConfig("on", ("d1", "root")), runner, gatewayProvider: () => IPAddress.Parse("192.0.2.1"));

        await guard.ReadAsync(CancellationToken.None);

        var probe = Assert.Single(runner.Probes).Script;
        Assert.Contains("getent passwd 'root'", probe);
        Assert.Contains("\"$PNR_HOME/.claude/settings.json\"", probe);
        Assert.DoesNotContain("/home/root/.claude", probe); // root's home is /root
        Assert.Contains("USER_HOME=$(getent passwd \"$USER_NAME\"", WslConfigGuard.BuildStartupScript("root", 18081));
    }

    [Fact]
    public async Task WslUserNameWithAQuoteStaysOneBashWord()
    {
        var runner = new FakeWslRunner { Installed = ["d1"], Running = ["d1"], ProbeOutput = _ => WslProbeInSync("o'neil") };
        var guard = new WslConfigGuard(WslConfig("on", ("d1", "o'neil")), runner, gatewayProvider: () => IPAddress.Parse("192.0.2.1"));

        await guard.ReadAsync(CancellationToken.None);

        var probe = Assert.Single(runner.Probes).Script;
        Assert.Contains("getent passwd 'o'\\''neil' | cut", probe);
        Assert.Contains("PNR_HOME='/home/o'\\''neil'", probe);
        var script = WslConfigGuard.BuildStartupScript("o'neil", 18081);
        Assert.Contains("USER_NAME='o'\\''neil'\n", script);
        // the status reads the name back unescaped
        Assert.Equal("o'neil", WslConfigGuard.ParseRead("SCRIPT=present\nUSER_NAME='o'\\''neil'\n").ScriptUser);
    }

    [Theory]
    [InlineData("""{"env":"http://old:1"}""")]
    [InlineData("""{"env":null}""")]
    [InlineData("""[1]""")]
    public async Task WslSyncRefusesADistroSettingsFileWhoseRootOrEnvIsNotAnObject(string json)
    {
        var runner = new FakeWslRunner
        {
            Installed = ["d1"],
            Running = ["d1"],
            ProbeOutput = _ => $"SCRIPT=missing\nPROXYENV=missing\nRUNNER=missing\nWSLCONF=missing\nSETTINGS=present\n{json}\nSETTINGS=END\n",
        };
        var guard = new WslConfigGuard(WslConfig("on", ("d1", "u1")), runner, gatewayProvider: () => IPAddress.Parse("192.0.2.1"));

        var result = await guard.SyncAsync(CancellationToken.None);

        var d1 = Assert.Single(result.Distros);
        Assert.True(d1.Failed);
        Assert.Contains("not a JSON object", d1.Reason);
        Assert.Empty(runner.Scripts); // nothing was written, the boot script refuses the same file
    }

    [Fact]
    public async Task WslProbeOmitsTheProxyEnvBlockForAnEmptyPath()
    {
        // `[ -f  ]` is true and a file-less `grep -E` reads the rest of the probe from stdin
        var runner = new FakeWslRunner
        {
            Installed = ["d1"],
            Running = ["d1"],
            ProbeOutput = _ => WslProbeInSync("u1"),
        };
        var config = WslConfig("on", ("d1", "u1"));
        config.Wsl.ProxyEnvPath = "";
        var guard = new WslConfigGuard(config, runner, gatewayProvider: () => IPAddress.Parse("192.0.2.1"));

        await guard.ReadAsync(CancellationToken.None);

        var probe = Assert.Single(runner.Probes).Script;
        Assert.DoesNotContain("PNR_CODEX_PROXY_PORT", probe);
        Assert.DoesNotContain("[ -f  ]", probe);
        Assert.Contains("PROXYENV=missing", probe);
    }

    [Fact]
    public async Task WslProbeOmitsTheStartupScriptBlockForAnEmptyPath()
    {
        var runner = new FakeWslRunner { Installed = ["d1"], Running = ["d1"], ProbeOutput = _ => WslProbeInSync("u1") };
        var config = WslConfig("on", ("d1", "u1"));
        config.Wsl.StartupScriptPath = "";
        var guard = new WslConfigGuard(config, runner, gatewayProvider: () => IPAddress.Parse("192.0.2.1"));

        await guard.ReadAsync(CancellationToken.None);

        var probe = Assert.Single(runner.Probes).Script;
        Assert.DoesNotContain("'^PORT='", probe);
        Assert.DoesNotContain("[ -f  ]", probe);
        Assert.DoesNotContain("[ -f '' ]", probe);
        Assert.Contains("echo SCRIPT=missing", probe);
    }

    [Fact]
    public async Task WslScriptsQuoteConfiguredPaths()
    {
        var runner = new FakeWslRunner
        {
            Installed = ["d1"],
            Running = ["d1"],
            ProbeOutput = _ => "SCRIPT=missing\nPROXYENV=missing\nRUNNER=missing\nWSLCONF=missing\nSETTINGS=missing\n",
            WriteOutput = "RUNNER=written\nWSLCONF=written\nSETTINGS=written\nSCRIPT=written\nENVFILE=written\nPNR=ok\n",
        };
        var config = WslConfig("on", ("d1", "u1"));
        config.Wsl.StartupScriptPath = "/etc/wsl d/startup/10 it's.sh";
        config.Wsl.ProxyEnvPath = "/etc/profile.d/20 pnr.sh";
        var guard = new WslConfigGuard(config, runner, gatewayProvider: () => IPAddress.Parse("192.0.2.1"));

        await guard.SyncAsync(CancellationToken.None);

        var probe = runner.Probes[0].Script;
        Assert.Contains("if [ -f '/etc/wsl d/startup/10 it'\\''s.sh' ]; then", probe);
        Assert.Contains("grep -E '^PORT=' '/etc/wsl d/startup/10 it'\\''s.sh'", probe);
        Assert.Contains("if [ -f '/etc/profile.d/20 pnr.sh' ]; then", probe);
        Assert.Contains("if [ -f '/etc/wsl d/startup.sh' ]; then", probe);
        Assert.Contains(runner.Scripts, s => s.Script.Contains("SCRIPT='/etc/wsl d/startup/10 it'\\''s.sh'"));
        Assert.Contains(runner.Scripts, s => s.Script.Contains("ls -1t \"$SCRIPT\".pnr-bak-*"));
        Assert.Contains(runner.Scripts, s => s.Script.Contains("ENVFILE='/etc/profile.d/20 pnr.sh'"));
        Assert.Contains(runner.Scripts, s => s.Script.Contains("ls -1t \"$ENVFILE\".pnr-bak-*"));
        Assert.Contains(runner.Scripts, s => s.Script.Contains("RUNNER='/etc/wsl d/startup.sh'"));
        Assert.All(runner.Scripts, s => Assert.DoesNotContain('\r', s.Script));
        Assert.DoesNotContain('\r', probe);
    }

    [Theory]
    [InlineData("/etc/wsl.d/startup/10-claude-proxy.sh", true)]
    [InlineData("/opt/wsl/startup/pnr.sh", true)]
    [InlineData("/usr/local/bin/pnr-proxy.sh", false)]
    [InlineData("/opt/x.sh", false)]
    [InlineData("/startup/x.sh", false)]
    [InlineData("startup/x.sh", false)]
    [InlineData("/etc/wsl.d/startup/", false)]
    [InlineData("", false)]
    public void StartupScriptPathMustLiveInADedicatedStartupDirectory(string path, bool expected)
        => Assert.Equal(expected, WslConfigGuard.IsDedicatedStartupScriptPath(path));

    [Fact]
    public async Task WslSyncRefusesAStartupScriptOutsideADedicatedDirectory()
    {
        var runner = new FakeWslRunner { Installed = ["d1"], Running = ["d1"], ProbeOutput = _ => "SCRIPT=missing\nPROXYENV=missing\nRUNNER=missing\nWSLCONF=missing\nSETTINGS=missing\n" };
        var config = WslConfig("on", ("d1", "u1"));
        config.Wsl.StartupScriptPath = "/usr/local/bin/pnr-proxy.sh";
        var guard = new WslConfigGuard(config, runner, gatewayProvider: () => IPAddress.Parse("192.0.2.1"));

        var result = await guard.SyncAsync(CancellationToken.None);

        var d1 = Assert.Single(result.Distros);
        Assert.True(d1.Failed);
        Assert.Contains("dedicated directory", d1.Reason);
        Assert.Empty(runner.Scripts); // nothing was written
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
        var runnerPayload = ExtractBase64Payload(boot.Script, "RUNNER='/etc/wsl.d/startup.sh'");
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

        // every script piped to bash -s (boot, ~/.claude owner, settings, startup, codex env), and
        // every base64 payload it embeds, is LF only — whatever the checkout the sources were compiled from
        Assert.Equal(5, runner.Scripts.Count);
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
        var userPayload = ExtractBase64Payload(boot.Script, "USERCMD='/etc/wsl.d/startup/00-user-boot-command.sh'");
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
        Assert.Null(result.Live.DirectBlocked); // the name is already blocked at DNS: the firewall layer was not exercised
        Assert.Contains("not exercised", result.Live.DirectDetail);
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

    [Fact]
    public void ElevatedBanOffWithAnUnterminatedHostsBlockStillRemovesTheOtherLayers()
    {
        var config = new AppConfig();
        config.Guard.HostsFile = Path.Combine(_tempDir, "hosts-unterminated");
        var hosts = $"127.0.0.1 localhost\r\n{config.Guard.HostsBeginMarker}\r\n0.0.0.0 a.example\r\n192.0.2.7 kept.example\r\n";
        File.WriteAllText(config.Guard.HostsFile, hosts);
        config.ForwardProxy.Listen = "127.0.0.1:18082";
        using var log = new FileLog(Path.Combine(_tempDir, "elev-unterminated.log"));

        var ops = new FakeBanSystemOps();
        var ban = Assert.ThrowsAny<IOException>(() => GuardElevatedCommands.ApplyBanLayers(config, log, ops, enable: false));
        Assert.Equal(["nrpt-off", "fw-off", "flush"], ops.Calls);
        Assert.Contains("hosts block was NOT removed", ban.Message);
        Assert.Contains("no matching end marker", ban.Message);
        Assert.Equal(hosts, File.ReadAllText(config.Guard.HostsFile)); // still refused to rewrite

        // "disable all": the policy goes too, the hosts failure is reported after it
        var allOps = new FakeBanSystemOps();
        var policy = new FakeEgressPolicyStore();
        var all = Assert.ThrowsAny<IOException>(() => GuardElevatedCommands.ApplyAll(config, log, allOps, policy, enable: false));
        Assert.Equal(["nrpt-off", "fw-off", "flush"], allOps.Calls);
        Assert.True(policy.Removed);
        Assert.Contains("egressProxyUrl and disableWslSessions removed", all.Message);
        Assert.Contains("hosts block was NOT removed", all.Message);
        Assert.Equal(hosts, File.ReadAllText(config.Guard.HostsFile));
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
