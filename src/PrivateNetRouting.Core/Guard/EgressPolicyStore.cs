using Microsoft.Win32;

namespace PrivateNetRouting.Core.Guard;

public sealed record EgressPolicyValues(bool KeyExists, string? EgressProxyUrl, int? DisableWslSessions);

/// <summary>
/// Managed-политика Claude Desktop (порт set-egress-proxy.ps1): HKLM\SOFTWARE\Policies\Claude —
/// egressProxyUrl (проксируется при любом способе запуска) и disableWslSessions=0 (наличие
/// любого значения в Policies переводит Claude в managed-режим, где WSL-сессии по умолчанию
/// запрещены; явный 0 на уровне машины возвращает их). Оба имени документированы Anthropic.
/// Ветка SOFTWARE\Policies защищена: запись — только администратор (элевейтнутый helper),
/// чтение доступно всем.
/// </summary>
public interface IEgressPolicyStore
{
    EgressPolicyValues Read();

    /// <summary>Требует прав администратора.</summary>
    void Apply(string proxyUrl);

    /// <summary>Требует прав администратора: снимает оба значения, пустой ключ удаляет целиком.</summary>
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
