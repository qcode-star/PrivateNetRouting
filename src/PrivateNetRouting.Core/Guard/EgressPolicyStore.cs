using Microsoft.Win32;

namespace PrivateNetRouting.Core.Guard;

public sealed record EgressPolicyValues(bool KeyExists, string? EgressProxyUrl, int? DisableWslSessions);

/// <summary>
/// Managed policy of Claude Desktop: HKLM\SOFTWARE\Policies\Claude —
/// egressProxyUrl (proxied no matter how the app is started) and disableWslSessions=0 (the presence
/// of any value under Policies puts Claude into managed mode, where WSL sessions are disabled by
/// default; an explicit 0 at the machine level brings them back). Both names are documented by Anthropic.
/// The SOFTWARE\Policies branch is protected: writing is administrator-only (elevated helper),
/// reading is available to everyone.
/// </summary>
public interface IEgressPolicyStore
{
    EgressPolicyValues Read();

    /// <summary>Requires administrator rights.</summary>
    void Apply(string proxyUrl);

    /// <summary>Requires administrator rights: removes both values, deletes the whole key if empty.</summary>
    void Remove();
}

public sealed class RegistryEgressPolicyStore : IEgressPolicyStore
{
    private const string KeyPath = @"SOFTWARE\Policies\Claude";
    private const string UrlValue = "egressProxyUrl";
    private const string WslValue = "disableWslSessions";

    public EgressPolicyValues Read()
    {
        using var key = Registry.LocalMachine.OpenSubKey(KeyPath);
        if (key is null)
        {
            return new EgressPolicyValues(false, null, null);
        }

        var url = key.GetValue(UrlValue) as string;
        var wsl = key.GetValue(WslValue) as int?;
        return new EgressPolicyValues(true, url, wsl);
    }

    public void Apply(string proxyUrl)
    {
        using var key = Registry.LocalMachine.CreateSubKey(KeyPath, writable: true);
        key.SetValue(UrlValue, proxyUrl, RegistryValueKind.String);
        key.SetValue(WslValue, 0, RegistryValueKind.DWord);
    }

    public void Remove()
    {
        using var key = Registry.LocalMachine.OpenSubKey(KeyPath, writable: true);
        if (key is null)
        {
            return;
        }

        key.DeleteValue(UrlValue, throwOnMissingValue: false);
        key.DeleteValue(WslValue, throwOnMissingValue: false);
        if (key.GetValueNames().Length == 0)
        {
            key.Close();
            Registry.LocalMachine.DeleteSubKey(KeyPath, throwOnMissingSubKey: false);
        }
    }
}
