using GatherBuddy.Config;
using GatherBuddy.Plugin;
using Newtonsoft.Json;

namespace GatherBuddy.Vulcan.Tests;

internal static class AfkPreventionTests
{
    public static void Run(Action<bool, string> require)
    {
        require(new Configuration().PreventAfkWhileAutomating
                && JsonConvert.DeserializeObject<Configuration>("{}")?.PreventAfkWhileAutomating == true,
            "AFK prevention must default on for new and existing configurations");

        require(AfkPrevention.ShouldResetTimer(
                enabled: true,
                isLoggedIn: true,
                isPvP: false,
                automationActive: true,
                autoAfkTimeLimit: 300f),
            "active automation must reset an enabled in-game AFK timer");
        require(!AfkPrevention.ShouldResetTimer(
                enabled: true,
                isLoggedIn: true,
                isPvP: false,
                automationActive: true,
                autoAfkTimeLimit: -1f),
            "the disabled in-game AFK setting must suppress timer resets");
        require(!AfkPrevention.ShouldResetTimer(
                enabled: false,
                isLoggedIn: true,
                isPvP: false,
                automationActive: true,
                autoAfkTimeLimit: 300f),
            "the plugin setting must disable AFK prevention");
        require(!AfkPrevention.ShouldResetTimer(
                enabled: true,
                isLoggedIn: true,
                isPvP: true,
                automationActive: true,
                autoAfkTimeLimit: 300f),
            "AFK prevention must remain disabled in PvP");
        require(!AfkPrevention.ShouldResetTimer(
                enabled: true,
                isLoggedIn: false,
                isPvP: false,
                automationActive: true,
                autoAfkTimeLimit: 300f)
            && !AfkPrevention.ShouldResetTimer(
                enabled: true,
                isLoggedIn: true,
                isPvP: false,
                automationActive: false,
                autoAfkTimeLimit: 300f),
            "AFK prevention must require a logged-in player and active automation");
    }
}
