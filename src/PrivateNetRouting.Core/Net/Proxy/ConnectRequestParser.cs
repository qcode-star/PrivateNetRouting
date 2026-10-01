namespace PrivateNetRouting.Core.Net.Proxy;

/// <summary>Разобранный CONNECT: хост нормализован (без хвостовой точки, нижний регистр).</summary>
public sealed record ConnectRequest(string Host, int Port)
{
    public override string ToString() => $"{Host}:{Port}";
}

/// <summary>
/// Разбор первой строки прокси-запроса: "CONNECT host:port HTTP/x.y". Поддерживается только
/// CONNECT (https-трафик); обычный HTTP получает 400 с записью в лог.
/// </summary>
public static class ConnectRequestParser
{
    public const int MaxHeaderBytes = 64 * 1024;
    public const int DefaultPort = 443;

    public static bool TryParse(ReadOnlySpan<byte> header, out ConnectRequest? request, out string? error)
    {
        request = null;
        error = null;
        var lineEnd = header.IndexOf((byte)'\r');
        if (lineEnd < 0)
        {
            error = "нет конца строки запроса";
            return false;
        }

        var line = System.Text.Encoding.Latin1.GetString(header[..lineEnd]);
        var parts = line.Split(' ', StringSplitOptions.TrimEntries);
        if (parts.Length != 3 || !parts[0].Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
        {
            error = $"не-CONNECT запрос: {Truncate(line)}";
            return false;
        }

        var authority = parts[1];
        int port;
        string host;
        var separator = authority.LastIndexOf(':');
        if (separator >= 0)
        {
            host = authority[..separator];
            if (!int.TryParse(authority[(separator + 1)..], out port) || port is < 1 or > 65535)
            {
                error = $"некорректный порт: {Truncate(authority)}";
                return false;
            }
        }
        else
        {
            host = authority;
            port = DefaultPort;
        }

        if (host.Length == 0)
        {
            error = "пустый хост";
            return false;
        }

        request = new ConnectRequest(host.TrimEnd('.').ToLowerInvariant(), port);
        return true;
    }

    private static string Truncate(string value) => value.Length <= 120 ? value : value[..120];
}
