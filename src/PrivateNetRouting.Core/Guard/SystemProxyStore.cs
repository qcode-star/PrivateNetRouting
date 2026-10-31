using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PrivateNetRouting.Core.Guard;

/// <summary>
/// WinINET system proxy: HKCU Internet Settings + InternetSetOption
/// (SETTINGS_CHANGED/REFRESH — otherwise applications pick up the setting only after a restart).
/// Requires no administrator rights; the address is the address of the application's forward proxy,
/// exceptions are local addresses, private ranges (masks like 10.*, WinINET does not understand CIDR),
/// corporate domains from the top-level bypassDomains (both forms) and *.tld entries from the
/// top-level directTlds. Enabling preserves whatever the owner had — ProxyServer, ProxyOverride and
/// the on/off state — once, and records the address this installation wrote; disabling restores the
/// owner's values and leaves a proxy it does not own alone.
/// </summary>
public interface ISystemProxyStore
{
    SystemProxyStatus Read();

    void Enable(string server, string bypass);

    /// <summary>
    /// Turns the switch off. A current ProxyServer that is ours (the recorded <c>ProxyServer.pnr-ours</c>
    /// address, or <paramref name="ourServer"/> as written by a build that predates the record) is
    /// restored from the save or cleared; a foreign one is left untouched — the switch is not ours.
    /// </summary>
    void Disable(string? ourServer = null);
}

public sealed class WinInetSystemProxyStore : ISystemProxyStore
{
    private const string DefaultKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    /// <summary>The address this installation wrote while on: the switch is ours only when it matches.</summary>
    private const string OursServerName = "ProxyServer.pnr-ours";

    private const string SavedServerName = "ProxyServer.pnr-saved";
    private const string SavedOverrideName = "ProxyOverride.pnr-saved";
    private const string SavedEnableName = "ProxyEnable.pnr-saved";

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

        var server = key.GetValue("ProxyServer") as string;
        return new SystemProxyStatus(
            key.GetValue("ProxyEnable") as int? == 1,
            server,
            key.GetValue("ProxyOverride") as string)
        {
            Ours = IsOurs(key, server),
        };
    }

    public void Enable(string server, string bypass)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_keyPath, writable: true);
        // whatever the owner has is preserved once: a re-enable of our own switch (an address
        // change) keeps the save, while a switch the owner replaced after a crashed cycle is saved
        // afresh, so the value they chose now is the one that comes back. A current value equal to
        // the address being written is ours too: a build that predates the ownership record (or a
        // crash before the record was written) leaves exactly that, and saving it would make a
        // later off "restore" our own proxy as the owner's
        var currentServer = key.GetValue("ProxyServer") as string;
        var saveHoldsCurrent = HasSave(key)
            && string.Equals(currentServer, key.GetValue(SavedServerName) as string, StringComparison.OrdinalIgnoreCase);
        var currentIsOurs = IsOurs(key, currentServer)
            || string.Equals(currentServer, server, StringComparison.OrdinalIgnoreCase);
        if (!currentIsOurs && !saveHoldsCurrent)
        {
            SaveCurrent(key, "ProxyServer", SavedServerName);
            SaveCurrent(key, "ProxyOverride", SavedOverrideName);
            key.SetValue(SavedEnableName, CurrentEnable(key) ? 1 : 0, RegistryValueKind.DWord);
        }

        // the record goes first: a crash between the two writes then leaves a record over the
        // owner's value (not ours, the save holds it), never our value without a record
        key.SetValue(OursServerName, server, RegistryValueKind.String);
        key.SetValue("ProxyServer", server, RegistryValueKind.String);
        key.SetValue("ProxyOverride", bypass, RegistryValueKind.String);
        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        NotifySettingsChanged();
    }

    public void Disable(string? ourServer = null)
    {
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath, writable: true);
        if (key is null)
        {
            return; // no key — nothing this installation could have written
        }

        var currentServer = key.GetValue("ProxyServer") as string;
        var ours = IsOurs(key, currentServer)
            || (ourServer is not null && string.Equals(currentServer, ourServer, StringComparison.OrdinalIgnoreCase));

        if (!HasSave(key))
        {
            if (!ours)
            {
                // a proxy this installation never wrote (the owner's, another tool's): the switch is
                // not ours to turn off and its values stay as they are
                return;
            }

            // nothing of the owner's to restore and the values on the switch are ours: they leave
            // with it
            key.DeleteValue("ProxyServer", throwOnMissingValue: false);
            key.DeleteValue("ProxyOverride", throwOnMissingValue: false);
            key.DeleteValue(OursServerName, throwOnMissingValue: false);
            key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
            NotifySettingsChanged();
            return;
        }

        if (!ours)
        {
            // the switch no longer holds our values (the owner or another tool replaced them): the
            // stale save must not overwrite them on a later off, so it goes together with the record
            ConsumeSave(key);
            return;
        }

        // the owner's values and on/off state come back, the save is consumed
        if (key.GetValue(SavedServerName) as string is { } savedServer)
        {
            key.SetValue("ProxyServer", savedServer, RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue("ProxyServer", throwOnMissingValue: false);
        }

        if (key.GetValue(SavedOverrideName) as string is { } savedOverride)
        {
            key.SetValue("ProxyOverride", savedOverride, RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue("ProxyOverride", throwOnMissingValue: false);
        }

        key.SetValue("ProxyEnable", key.GetValue(SavedEnableName) as int? == 1 ? 1 : 0, RegistryValueKind.DWord);
        key.DeleteValue(OursServerName, throwOnMissingValue: false);
        ConsumeSave(key);
        NotifySettingsChanged();
    }

    private static bool IsOurs(RegistryKey key, string? server) =>
        server is not null
        && key.GetValue(OursServerName) is string ours
        && string.Equals(server, ours, StringComparison.OrdinalIgnoreCase);

    private static bool CurrentEnable(RegistryKey key) => key.GetValue("ProxyEnable") as int? == 1;

    private static bool HasSave(RegistryKey key) =>
        key.GetValue(SavedServerName) is not null
        || key.GetValue(SavedOverrideName) is not null
        || key.GetValue(SavedEnableName) is not null;

    private static void ConsumeSave(RegistryKey key)
    {
        key.DeleteValue(SavedServerName, throwOnMissingValue: false);
        key.DeleteValue(SavedOverrideName, throwOnMissingValue: false);
        key.DeleteValue(SavedEnableName, throwOnMissingValue: false);
        key.DeleteValue(OursServerName, throwOnMissingValue: false);
    }

    /// <summary>The current value becomes the saved one; an absent current value clears the save.</summary>
    private static void SaveCurrent(RegistryKey key, string name, string savedName)
    {
        if (key.GetValue(name) is string value && value.Length > 0)
        {
            key.SetValue(savedName, value, RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(savedName, throwOnMissingValue: false);
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
