using System.Text.Json;
using PrivateNetRouting.Core.Configuration;
using PrivateNetRouting.Core.Logging;

namespace PrivateNetRouting.Core.Updater;

/// <summary>The offered-version source for the Codex update check; null — no version is known.</summary>
public interface ICodexVersionManifest
{
    Task<string?> FindOfferedVersionAsync(CancellationToken ct);
}

/// <summary>
/// OpenAI's own version manifest of the Codex Store package (codex.updateVersionUrl — the same
/// endpoint the codex CLI's doctor reads): {"schemaVersion","buildVersion","storeProductId",
/// "packageIdentity"}. The manifest only NAMES the offered version — the bits always come from
/// the Store. The host sits under the direct-access ban, so the lookup makes sense only through
/// the live proxy chain (the caller's HttpClient is already proxied); any failure is "no version",
/// never an error — the Store search remains the sole source of truth about an update existing.
/// </summary>
public sealed class CodexVersionManifest(
    HttpClient http,
    CodexUpdateConfig config,
    FileLog log,
    TimeSpan? requestTimeout = null) : ICodexVersionManifest
{
    /// <summary>The HttpClient is shared and has no global timeout: a stalled egress must not hang the check.</summary>
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(30);

    public async Task<string?> FindOfferedVersionAsync(CancellationToken ct)
    {
        if (config.UpdateVersionUrl.Length == 0)
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(requestTimeout ?? DefaultRequestTimeout);
        try
        {
            using var response = await http.GetAsync(config.UpdateVersionUrl, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return ParseVersion(json, config.StoreProductId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            log.Debug("codex-updater: version manifest timed out");
            return null;
        }
        catch (Exception ex)
        {
            log.Debug($"codex-updater: version manifest unreachable: {ex.Message}");
            return null;
        }
    }

    internal static string? ParseVersion(string json, string expectedStoreProductId)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.TryGetProperty("storeProductId", out var id)
                && id.ValueKind == JsonValueKind.String
                && !string.Equals(id.GetString(), expectedStoreProductId, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return root.TryGetProperty("buildVersion", out var version)
                && version.ValueKind == JsonValueKind.String
                ? version.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
