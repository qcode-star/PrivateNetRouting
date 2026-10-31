using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PrivateNetRouting.Core.Guard;

/// <summary>
/// WinINET system proxy: HKCU Internet Settings + InternetSetOption
/// (SETTINGS_CHANGED/REFRESH — otherwise applications pick up the setting only after a restart).
/// Requires no administrator rights; the address is the address of the application's forward proxy,
/// exceptions are local addresses, private ranges (masks like 10.*, WinINET does not understand CIDR),
/// corporate domains from the top-level bypassDomains (both forms) and *.tld entries from the
/// top-level directTlds. Enabling preserves the user's own ProxyServer/ProxyOverride once (the
/// first enable while the switch is off), disabling restores them.
/// </summary>
public interface ISystemProxyStore
{
    SystemProxyStatus Read();

    void Enable(string server, string bypass);

    /// <summary>
    /// Turns the switch off. <paramref name="ourServer"/> is the address this installation writes
    /// while on: when the current ProxyServer matches it, the values are ours and leave with the
    /// switch; anything else (the user's own, restored earlier) is left alone.
    /// </summary>
    void Disable(string? ourServer = null);
}

public sealed class WinInetSystemProxyStore : ISystemProxyStore
{
    private const string DefaultKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
    private const string SavedServerName = "ProxyServer.pnr-saved";
    private const string SavedOverrideName = "ProxyOverride.pnr-saved";

    /// <summary>A test run exercises the same logic on its own subkey instead of the live one.</summary>
    private readonly string _keyPath;

    public WinInetSystemProxyStore(string? keyPath = null) => _keyPath = keyPath ?? DefaultKeyPath;

    public SystemProxyStatus Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
        if (key is null)
        {
            return new SystemProxyStatus(false, null, null);
        }

        return new SystemProxyStatus(
            key.GetValue("ProxyEnable") as int? == 1,
            key.GetValue("ProxyServer") as string,
            key.GetValue("ProxyOverride") as string);
    }

    public void Enable(string server, string bypass)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_keyPath, writable: true);
        // the user's own settings are preserved once: the first enable while the switch is off
        // saves them; a re-enable (an address change) or a leftover save of a crashed cycle does
        // not overwrite it
        var hasSave = key.GetValue(SavedServerName) is not null || key.GetValue(SavedOverrideName) is not null;
        if (key.GetValue("ProxyEnable") as int? != 1 && !hasSave)
        {
            SaveIfPresent(key, "ProxyServer", SavedServerName);
            SaveIfPresent(key, "ProxyOverride", SavedOverrideName);
        }

        key.SetValue("ProxyServer", server, RegistryValueKind.String);
        key.SetValue("ProxyOverride", bypass, RegistryValueKind.String);
        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        NotifySettingsChanged();
    }

    public void Disable(string? ourServer = null)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_keyPath, writable: true);
        var savedServer = key.GetValue(SavedServerName) as string;
        var savedOverride = key.GetValue(SavedOverrideName) as string;
        if (savedServer is not null || savedOverride is not null)
        {
            // the user's own settings come back, the save is consumed
            if (savedServer is not null)
            {
                key.SetValue("ProxyServer", savedServer, RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue("ProxyServer", throwOnMissingValue: false);
            }

            if (savedOverride is not null)
            {
                key.SetValue("ProxyOverride", savedOverride, RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue("ProxyOverride", throwOnMissingValue: false);
            }

            key.DeleteValue(SavedServerName, throwOnMissingValue: false);
            key.DeleteValue(SavedOverrideName, throwOnMissingValue: false);
        }
        else if (ourServer is not null
            && string.Equals(key.GetValue("ProxyServer") as string, ourServer, StringComparison.OrdinalIgnoreCase))
        {
            // nothing of the user's to restore and the values on the switch are ours: they leave
            // with it (a second off, or an enable that had nothing to save)
            key.DeleteValue("ProxyServer", throwOnMissingValue: false);
            key.DeleteValue("ProxyOverride", throwOnMissingValue: false);
        }

        key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        NotifySettingsChanged();
    }

    private static void SaveIfPresent(RegistryKey key, string name, string savedName)
    {
        if (key.GetValue(name) is string value && value.Length > 0)
        {
            key.SetValue(savedName, value, RegistryValueKind.String);
        }
    }

    private static void NotifySettingsChanged()
    {
        _ = InternetSetOption(IntPtr.Zero, InternetOptionSettingsChanged, IntPtr.Zero, 0);
        _ = InternetSetOption(IntPtr.Zero, InternetOptionRefresh, IntPtr.Zero, 0);
    }

    private const int InternetOptionSettingsChanged = 39;
    private const int InternetOptionRefresh = 37;

    [DllImport("wininet.dll", SetLastError = true, CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);
}

/// <summary>Builds the ProxyOverride value from base entries, the bypass zones and the direct TLDs.</summary>
public static class SystemProxyBypass
{
    /// <summary>Base entries are fixed in code (protocol constants, not environment).</summary>
    private static readonly string[] BaseParts =
    [
        "localhost", "127.0.0.1", "<local>", "*.local",
        "10.*", "192.168.*", "169.254.*", "100.64.*",
    ];

    public static string Build(IEnumerable<string> bypassDomains, IEnumerable<string> directTlds)
    {
        var parts = new List<string>(BaseParts);
        for (var i = 16; i <= 31; i++)
        {
            parts.Add($"172.{i}.*");
        }

        parts.AddRange(directTlds.Select(TldBypassPart));
        foreach (var domain in bypassDomains)
        {
            if (string.IsNullOrWhiteSpace(domain))
            {
                continue;
            }

            parts.Add(domain);
            parts.Add("*." + domain);
        }

        return string.Join(";", parts);
    }

    /// <summary>A direct TLD becomes the bypass form "*.tld" (an already-spelled bypass entry is kept as is).</summary>
    internal static string TldBypassPart(string tld)
    {
        var trimmed = tld.Trim();
        return trimmed.StartsWith("*.") ? trimmed : "*." + trimmed.TrimStart('*', '.');
    }
}
