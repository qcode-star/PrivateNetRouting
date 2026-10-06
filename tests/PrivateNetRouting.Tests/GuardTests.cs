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

    /// <summary>Answer of the root pass (state read) by distro name.</summary>
    public Func<string, string> ProbeOutput { get; set; } = _ => "SCRIPT=missing\nSETTINGS=missing\n";

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
            // the reading probe (echo SCRIPT=…) answers with the canned output;
            // the writing scripts (settings/startup) are only recorded
            if (standardInput.Contains("echo SCRIPT="))
            {
                return Task.FromResult(ProbeOutput(distro));
            }

            Scripts.Add((distro, user, standardInput));
            return Task.FromResult("");
        }

        return Task.FromResult(ProbeOutput(distro));
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
        Assert.Equal("http://127.0.0.1:18082", ClaudeEnvValues.WindowsProxyUrl(config.ForwardProxy));
        Assert.Equal(18082, ClaudeEnvValues.Port(config.ForwardProxy));
        Assert.Null(ClaudeEnvValues.WslProxyUrl(null, config.ForwardProxy));
        Assert.Equal("http://192.0.2.1:18082", ClaudeEnvValues.WslProxyUrl(IPAddress.Parse("192.0.2.1"), config.ForwardProxy));
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
        config.Launcher.BypassDomains = ["corp.example.com"];
        config.Guard.ClaudeSettingsPath = Path.Combine(_tempDir, "settings.json");
        return config;
    }

    [Fact]
    public void SettingsSyncPreservesKeysAndBackups()
    {
        var config = SettingsConfig();
        File.WriteAllText(config.Guard.ClaudeSettingsPath, """
            {
              "other": 1,
              "env": { "HTTPS_PROXY": "http://old:1", "FOO": "bar" }
            }
            """);

        var guard = new ClaudeSettingsGuard(config);
        var backup = guard.Sync();

        Assert.NotNull(backup);
        Assert.True(File.Exists(backup));
        var status = guard.Read();
        Assert.Equal(GuardState.On, status.State);

        var json = JsonNode.Parse(File.ReadAllText(config.Guard.ClaudeSettingsPath))!;
        Assert.Equal(1, (int)json["other"]!);
        Assert.Equal("bar", (string?)json["env"]!["FOO"]);
        Assert.Equal("http://127.0.0.1:18082", (string?)json["env"]!["HTTPS_PROXY"]);
        Assert.Equal("http://127.0.0.1:18082", (string?)json["env"]!["HTTP_PROXY"]);
        Assert.Equal("localhost,127.0.0.1,.local,corp.example.com,.corp.example.com", (string?)json["env"]!["NO_PROXY"]);
    }

    [Fact]
    public void SettingsStatusDetectsMissingMismatchAndAbsent()
    {
        var config = SettingsConfig();
        var guard = new ClaudeSettingsGuard(config);

        Assert.True(guard.Read().FileMissing); // the file is missing
        guard.Sync();
        Assert.Equal(GuardState.On, guard.Read().State);

        File.WriteAllText(config.Guard.ClaudeSettingsPath, """{ "env": { "HTTPS_PROXY": "http://other:2" } }""");
        var mismatch = guard.Read();
        Assert.Equal(GuardState.Partial, mismatch.State);
        Assert.Equal("http://other:2", mismatch.CurrentProxy);
    }

    // ----- NRPT/firewall PowerShell scripts -----

    [Fact]
    public void NrptScriptCoversBothFormsAndEscapesQuotes()
    {
        var config = new AppConfig();
        config.Guard.NrptRuleName = "ai'direct";
        config.Guard.BanTrees = ["claude.ai", "openai.com"];
        var ops = new PowerShellBanSystemOps(config.Guard, new StubPowerShell());

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
        config.Guard.FirewallSubnets = ["160.79.104.0/21", "2607:6bc0::/48", "2607:6bc0:11::/48"];
        var ops = new PowerShellBanSystemOps(config.Guard, new StubPowerShell());

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

    private AppConfig WslConfig(string mode, params (string Name, string User, bool Sync)[] distros)
    {
        var config = new AppConfig();
        config.Guard.Wsl.Mode = mode;
        config.Guard.Wsl.Distros = distros
            .Select(d => new WslDistroConfig { Name = d.Name, User = d.User, Sync = d.Sync })
            .ToList();
        return config;
    }

    [Fact]
    public async Task WslLayerModes()
    {
        var off = new WslConfigGuard(WslConfig("off", ("d1", "u1", true)), new FakeWslRunner());
        var (enabled, reason) = await off.EvaluateLayerAsync(CancellationToken.None);
        Assert.False(enabled);
        Assert.Contains("mode=off", reason);

        var empty = new WslConfigGuard(WslConfig("on"), new FakeWslRunner());
        Assert.False((await empty.EvaluateLayerAsync(CancellationToken.None)).Enabled);

        var runner = new FakeWslRunner { Installed = ["d1", "d2"], Running = ["d1"] };
        var on = new WslConfigGuard(WslConfig("on", ("d9", "u9", true)), runner);
        Assert.True((await on.EvaluateLayerAsync(CancellationToken.None)).Enabled); // on does not look at installation

        var auto = new WslConfigGuard(WslConfig("auto", ("d9", "u9", true)), runner);
        Assert.False((await auto.EvaluateLayerAsync(CancellationToken.None)).Enabled); // d9 is not installed

        var autoMatch = new WslConfigGuard(WslConfig("auto", ("d1", "u1", true)), runner);
        Assert.True((await autoMatch.EvaluateLayerAsync(CancellationToken.None)).Enabled);
    }

    [Fact]
    public async Task WslStatusAggregatesPerDistro()
    {
        var runner = new FakeWslRunner
        {
            Installed = ["d1", "d2"],
            Running = ["d1"], // d2 is not running; not started just for status
            ProbeOutput = distro => distro == "d1"
                ? $"SCRIPT=present\nPORT=18082\nUSER_NAME='u1'\nSETTINGS=present\n{{\"env\":{{\"HTTPS_PROXY\":\"http://192.0.2.1:18082\"}}}}\nSETTINGS=END\n"
                : "",
        };
        var guard = new WslConfigGuard(
            WslConfig("on", ("d1", "u1", true), ("d2", "u2", false)),
            runner,
            gatewayProvider: () => IPAddress.Parse("192.0.2.1"));

        var status = await guard.ReadAsync(CancellationToken.None);
        Assert.Equal(GuardState.Unknown, status.State); // d2 is not running, so unknown
        Assert.Equal(2, status.Distros.Count);
        Assert.Equal(GuardState.On, status.Distros[0].State);
        Assert.Equal(18082, status.Distros[0].ScriptPort);
        Assert.Equal("http://192.0.2.1:18082", status.Distros[0].CurrentProxy);
        Assert.Equal(GuardState.Unknown, status.Distros[1].State);
        Assert.Contains("not running", status.Distros[1].Reason);
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
            WslConfig("on", ("d1", "u1", true), ("d3", "u3", true)),
            runner,
            gatewayProvider: () => IPAddress.Parse("192.0.2.1"));

        var status = await guard.ReadAsync(CancellationToken.None);
        Assert.Equal(GuardState.Partial, status.State);
        Assert.Equal(GuardState.Partial, status.Distros[0].State);
        Assert.Contains("script PORT=8082", status.Distros[0].Reason);
        Assert.Equal(GuardState.Off, status.Distros[1].State); // no script
    }

    [Fact]
    public async Task WslSyncWritesScriptAndSettingsOnlyForSyncedRunningDistros()
    {
        var runner = new FakeWslRunner
        {
            Installed = ["d1", "d2", "d3"],
            Running = ["d1", "d3"], // d2 is not running, skip without bringing it up
            ProbeOutput = _ => "SCRIPT=present\nPORT=8082\nSETTINGS=present\n{}\nSETTINGS=END\n",
        };
        var guard = new WslConfigGuard(
            WslConfig("on", ("d1", "u1", true), ("d2", "u2", true), ("d3", "u3", false)),
            runner,
            gatewayProvider: () => IPAddress.Parse("192.0.2.1"));

        var result = await guard.SyncAsync(CancellationToken.None);

        Assert.False(result.LayerDisabled);
        Assert.Equal(2, result.Distros.Count); // sync=false (d3) is not synced
        var d1 = Assert.Single(result.Distros, d => d.Distro == "d1");
        var d2 = Assert.Single(result.Distros, d => d.Distro == "d2");
        Assert.False(d1.Skipped);
        Assert.Equal("http://192.0.2.1:18082", d1.ProxyUrl);
        Assert.True(d2.Skipped);
        Assert.Contains("not running", d2.Reason);

        // d1: two passes, settings as the user and the script as root
        var settings = Assert.Single(runner.Scripts, s => s.Distro == "d1" && s.User == "u1");
        Assert.Contains("base64 -d > ~/.claude/settings.json", settings.Script);
        var root = Assert.Single(runner.Scripts, s => s.Distro == "d1" && s.User == "root");
        Assert.Contains("/etc/wsl.d/startup/10-claude-proxy.sh", root.Script);
        Assert.DoesNotContain("chmod 755", settings.Script);

        // startup script contents: the app port and the distro user
        var payload = ExtractBase64Payload(root.Script, "/etc/wsl.d/startup/10-claude-proxy.sh");
        var script = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
        Assert.Contains("USER_NAME='u1'", script);
        Assert.Contains("PORT=18082", script);
        Assert.Contains("env['HTTPS_PROXY'] = proxy", script);
        Assert.DoesNotContain("env['NO_PROXY']", script); // the startup script does not touch NO_PROXY (mentioned only in a comment)
    }

    [Fact]
    public async Task WslSyncSkipsWholeLayerWhenDisabled()
    {
        var runner = new FakeWslRunner { Installed = ["d1"], Running = ["d1"] };
        var guard = new WslConfigGuard(WslConfig("off", ("d1", "u1", true)), runner);

        var result = await guard.SyncAsync(CancellationToken.None);
        Assert.True(result.LayerDisabled);
        Assert.Empty(runner.Scripts);
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
        config.Guard.HostsFile = Path.Combine(_tempDir, "hosts");
        config.Guard.BanProbeName = "probe.invalid";
        File.WriteAllText(config.Guard.HostsFile, "127.0.0.1 localhost\r\n");
        var banOps = new FakeBanSystemOps
        {
            Layers = new BanLayersState(1, [".claude.ai"], 2, ["160.79.104.0/21", "2607:6bc0::/48"]),
        };
        HostsFileBlock.Apply(config.Guard.HostsFile, config.Guard.HostsBeginMarker, config.Guard.HostsEndMarker, config.Guard.HostsComment, config.Guard.BanHosts);
        var service = MakeService(
            config,
            new FakeEgressPolicyStore { Values = new EgressPolicyValues(true, "http://127.0.0.1:18082", 0) },
            banOps,
            new FakeSystemProxyStore { Status = new SystemProxyStatus(true, "127.0.0.1:18082", "localhost") },
            new FakeElevatedRunner(),
            out _);

        var snapshot = await service.GetStatusAsync(CancellationToken.None);
        Assert.Equal(GuardState.On, snapshot.Ban.State);
        Assert.Equal(config.Guard.BanHosts.Count, snapshot.Ban.HostsEntries);
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
        config.Guard.HostsFile = Path.Combine(_tempDir, "hosts-empty");
        config.Guard.BanProbeName = "probe.invalid";
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
        config.Guard.BanProbeName = "probe.invalid"; // does not resolve, so the checks are deterministic
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
        var config = new AppConfig { Guard = new GuardConfig { HostsFile = Path.Combine(_tempDir, "h"), BanProbeName = "probe.invalid" } };
        var service = MakeService(config, new FakeEgressPolicyStore(), new FakeBanSystemOps(), new FakeSystemProxyStore(),
            new FakeElevatedRunner(ok: false, detail: "UAC declined"), out _);

        await Assert.ThrowsAsync<IOException>(() => service.BanAsync(true, CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(() => service.PolicyAsync(false, CancellationToken.None));
    }

    [Fact]
    public async Task SystemProxyOnUsesListenerAddress()
    {
        var config = new AppConfig();
        var sysProxy = new FakeSystemProxyStore();
        var service = MakeService(config, new FakeEgressPolicyStore(), new FakeBanSystemOps(), sysProxy, new FakeElevatedRunner(), out _);

        await service.SetSystemProxyAsync(true, CancellationToken.None);
        Assert.Equal(("127.0.0.1:18082", SystemProxyBypass.Build([], config.Guard.SystemProxyExtraBypass)), sysProxy.EnabledWith);

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
        config.Guard.BanProbeName = "probe.invalid";
        var ops = new FakeBanSystemOps();
        using var log = new FileLog(Path.Combine(_tempDir, "elev.log"));

        var detail = GuardElevatedCommands.ApplyBanLayers(config, log, ops, enable: true);

        Assert.Equal(["nrpt-on", "fw-on", "flush"], ops.Calls); // hosts is applied directly via the file
        var hosts = HostsFileBlock.Read(config.Guard.HostsFile, config.Guard.HostsBeginMarker, config.Guard.HostsEndMarker, config.Guard.HostsComment, config.Guard.BanHosts);
        Assert.True(hosts.Present);
        Assert.Equal(config.Guard.BanHosts.Count, hosts.EntryCount);
        Assert.Contains("NRPT rule on", detail);

        var removeDetail = GuardElevatedCommands.ApplyBanLayers(config, log, ops, enable: false);
        Assert.Equal(["nrpt-on", "fw-on", "flush", "nrpt-off", "fw-off", "flush"], ops.Calls);
        Assert.DoesNotContain(">>", File.ReadAllText(config.Guard.HostsFile));
        Assert.Contains("removed", removeDetail);
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
