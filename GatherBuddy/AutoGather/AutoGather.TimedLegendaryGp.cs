using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using GatherBuddy.Classes;
using GatherBuddy.Enums;
using GatherBuddy.Helpers;
using GatherBuddy.AutoGather.Lists;
using GatherBuddy.Time;
using GatherBuddy.Utilities;
using System;
using System.Numerics;

namespace GatherBuddy.AutoGather;

public partial class AutoGather
{
    private bool ShouldWaitForUpcomingLegendaryGp(
        GatherTarget target,
        ConfigPreset config,
        IGameObject gameObject)
    {
        if (!config.ChooseBestActionsAutomatically
         || target.Gatherable == null
         || target.Node?.NodeType != NodeType.Legendary)
            return false;

        var hSeparation = Vector2.Distance(gameObject.Position.ToVector2(), Player.Position.ToVector2());
        var vSeparation = Math.Abs(gameObject.Position.Y - Player.Position.Y);
        if (hSeparation >= 3.5 || vSeparation >= 3)
            return false;

        var player = Player.Object;
        if (player == null)
            return false;

        var now = GatherBuddy.Time.ServerTime;
        if (!_activeItemList.TryGetUpcomingLegendaryWindow(target, now, out var upcomingLegendaryWindow)
         || !TimedLegendaryGpPolicy.ShouldWaitForFullGp(
                target.Time,
                upcomingLegendaryWindow,
                now,
                (int)player.CurrentGp,
                (int)player.MaxGp,
                GetGpRegenPerTick()))
            return false;

        if (Dalamud.Conditions[ConditionFlag.Mounted])
        {
            EnqueueDismount();
            TaskManager.Enqueue(() =>
            {
                if (Dalamud.Conditions[ConditionFlag.Mounted]
                 && Dalamud.Conditions[ConditionFlag.InFlight]
                 && !Dalamud.Conditions[ConditionFlag.Diving])
                    ForceLandAndDismount();
            });
        }
        else
        {
            StopNavigation();
        }

        AutoStatus = "Waiting for GP before the next legendary node...";
        return true;
    }

    private static int GetGpRegenPerTick()
    {
        const uint gpRegenQuestId = 68160;
        if (!QuestManager.IsQuestComplete(gpRegenQuestId))
            return 5;

        return Player.Level switch
        {
            >= 83 => 8,
            >= 80 => 7,
            _ => 6,
        };
    }
}
