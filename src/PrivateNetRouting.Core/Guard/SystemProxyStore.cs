using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PrivateNetRouting.Core.Guard;

/// <summary>
/// WinINET system proxy (port of proxy-on/off.ps1): HKCU Internet Settings + InternetSetOption
/// (SETTINGS_CHANGED/REFRESH — otherwise applications pick up the setting only after a restart).
/// Requires no administrator rights; the address is the address of the application's forward proxy,
/// exceptions are local addresses, private ranges (masks like 10.*, WinINET does not understand CIDR),
/// extra zones from the config and corporate domains (launcher.bypassDomains, both forms).
/// </summary>
public interface ISystemProxyStore
{
    SystemProxyStatus Read();

    void Enable(string server, string bypass);

    void Disable();
}

public sealed class WinInetSystemProxyStore : ISystemProxyStore
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    public SystemProxyStatus Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
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
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        key.SetValue("ProxyServer", server, RegistryValueKind.String);
        key.SetValue("ProxyOverride", bypass, RegistryValueKind.String);
        key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
        NotifySettingsChanged();
    }

    public void Disable()
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
        NotifySettingsChanged();
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

/// <summary>Builds the ProxyOverride value from base entries, config zones and extra zones.</summary>
public static class SystemProxyBypass
{
    /// <summary>Base entries are fixed in code (protocol constants, not environment).</summary>
    private static readonly string[] BaseParts =
    [
        "localhost", "127.0.0.1", "<local>", "*.local",
        "10.*", "192.168.*", "169.254.*", "100.64.*",
    ];

    public static string Build(IEnumerable<string> bypassDomains, IEnumerable<string> extraParts)
    {
        var parts = new List<string>(BaseParts);
        for (var i = 16; i <= 31; i++)
        {
            parts.Add($"172.{i}.*");
        }

        parts.AddRange(extraParts);
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
}
