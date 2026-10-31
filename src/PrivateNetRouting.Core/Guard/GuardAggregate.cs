namespace PrivateNetRouting.Core.Guard;

/// <summary>
/// The collapsed Guards card folds the layer states into one: a summary state and the "all on"
/// test that picks between "Enable all" and "Disable all". A layer without a verdict to give — the
/// WSL layer switched off by the config, or every configured distro stopped or absent — does not
/// vote: it cannot be "on", and counting it as a mix would leave users without WSL (the default
/// config) with an "Enable all" that re-prompts UAC forever.
/// </summary>
public static class GuardAggregate
{
    /// <summary>Two states as one: equals stay, anything mixed is partial, unknown dominates.</summary>
    public static GuardState Merge(GuardState a, GuardState b)
    {
        if (a == b)
        {
            return a;
        }

        return a == GuardState.Unknown || b == GuardState.Unknown ? GuardState.Unknown : GuardState.Partial;
    }

    /// <summary>
    /// Claude Code and Codex settings are one layer to the owner; an application off in the config
    /// has no vote. Both off — nothing to judge: null (no vote).
    /// </summary>
    public static GuardState? SettingsLayer(GuardSnapshot snapshot) => (snapshot.ClaudeEnabled, snapshot.CodexEnabled) switch
    {
        (true, true) => Merge(snapshot.Settings.State, snapshot.CodexSettings.State),
        (true, false) => snapshot.Settings.State,
        (false, true) => snapshot.CodexSettings.State,
        _ => null,
    };

    /// <summary>The WSL layer's state over the distros that can be judged; null — nothing to judge (no vote).</summary>
    public static GuardState? WslLayer(WslConfigStatus wsl)
    {
        var judged = wsl.Distros.Where(d => !d.Inactive).ToList();
        return judged.Count == 0 ? null : WslConfigGuard.Aggregate(judged);
    }

    /// <summary>The layers that vote, in card order.</summary>
    private static IEnumerable<GuardState> Votes(GuardSnapshot snapshot)
    {
        yield return snapshot.Ban.State;
        yield return snapshot.Policy.State;
        if (SettingsLayer(snapshot) is { } settings)
        {
            yield return settings;
        }

        if (WslLayer(snapshot.Wsl) is { } wsl)
        {
            yield return wsl;
        }
    }

    /// <summary>The one status of the collapsed card; unknown dominates, any mix is partial, only all-on is good.</summary>
    public static GuardState Summary(GuardSnapshot snapshot) => Votes(snapshot).Aggregate(Merge);

    /// <summary>Every voting layer is on: the card's single action is then "Disable all".</summary>
    public static bool AllOn(GuardSnapshot snapshot) => Votes(snapshot).All(state => state == GuardState.On);
}
