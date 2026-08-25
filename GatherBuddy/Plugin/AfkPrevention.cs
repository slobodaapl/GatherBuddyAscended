using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace GatherBuddy.Plugin;

internal static class AfkPrevention
{
    public static unsafe void Update(bool enabled, bool automationActive)
    {
        var inputTimer = InputTimerModule.Instance();
        if (inputTimer == null
            || !ShouldResetTimer(
                enabled,
                Dalamud.ClientState.IsLoggedIn,
                Dalamud.ClientState.IsPvP,
                automationActive,
                inputTimer->AutoAfkTimeLimit))
        {
            return;
        }

        if (inputTimer->AfkTimer != 0f)
            inputTimer->AfkTimer = 0f;
    }

    internal static bool ShouldResetTimer(
        bool enabled,
        bool isLoggedIn,
        bool isPvP,
        bool automationActive,
        float autoAfkTimeLimit)
        => enabled
            && isLoggedIn
            && !isPvP
            && automationActive
            && autoAfkTimeLimit >= 0f;
}
