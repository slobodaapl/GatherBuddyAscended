using FFXIVClientStructs.FFXIV.Client.Game.UI;
using GatherBuddy.Plugin;

namespace GatherBuddy.SeFunctions;

public static unsafe class Teleporter
{
    public static bool IsAttuned(uint aetheryte)
    {
        if (aetheryte == 0 || !Dalamud.ClientState.IsLoggedIn)
            return false;

        var aetherytes = Dalamud.Aetherytes;
        for (var i = 0; i < aetherytes.Length; i++)
        {
            var entry = aetherytes[i];
            if (entry?.AetheryteId == aetheryte)
                return true;
        }

        return false;
    }

    public static bool TryGetTeleportCost(uint aetheryte, out uint gilCost)
    {
        gilCost = 0;
        if (aetheryte == 0 || !Dalamud.ClientState.IsLoggedIn)
            return false;

        var aetherytes = Dalamud.Aetherytes;
        for (var i = 0; i < aetherytes.Length; i++)
        {
            var entry = aetherytes[i];
            if (entry == null || entry.AetheryteId != aetheryte)
                continue;

            gilCost = entry.GilCost;
            return true;
        }

        return false;
    }

    public static bool Teleport(uint aetheryte)
    {
        if (!IsAttuned(aetheryte))
        {
            Communicator.PrintError("Could not teleport to ",
                GatherBuddy.GameData.Aetherytes.TryGetValue(aetheryte, out var a) ? a.Name : "Unknown Aetheryte", GatherBuddy.Config.SeColorNames,
                " not attuned.");
            return false;
        }

        var telepo = Telepo.Instance();
        if (telepo == null)
        {
            GatherBuddy.Log.Error("Could not teleport: Telepo is missing.");
            return false;
        }

        telepo->Teleport(aetheryte, 0);
        return true;
    }

    // Teleport without checking for attunement. Use at own risk.
    public static void TeleportUnchecked(uint aetheryte)
    {
        var telepo = Telepo.Instance();
        if (telepo == null)
        {
            GatherBuddy.Log.Error("Could not teleport: Telepo is missing.");
            return;
        }

        telepo->Teleport(aetheryte, 0);
    }
}
