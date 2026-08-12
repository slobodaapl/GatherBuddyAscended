using System;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.Game.Group;
using GatherBuddy.Plugin;

namespace GatherBuddy.Helpers;

public static unsafe class HomeNavigationHelper
{
    public static bool ShouldReturnHomeAfterCollectables()
        => GatherBuddy.Config.AutoGatherConfig.GoHomeWhenIdle;

    public static bool TryStartReturnHome(out string? error)
    {
        error = null;
        if (Dalamud.Conditions[ConditionFlag.BoundByDuty])
        {
            error = "Cannot return home while bound by duty.";
            return false;
        }

        if (!Lifestream.Enabled)
        {
            error = "Lifestream is not available.";
            return false;
        }

        if (Lifestream.IsBusy())
            return false;

        var command = GatherBuddy.Config.AutoGatherConfig.LifestreamCommand;
        if (string.IsNullOrWhiteSpace(command))
            command = "auto";
        if (command.Contains("/li ", StringComparison.OrdinalIgnoreCase))
            command = command.Replace("/li ", string.Empty, StringComparison.OrdinalIgnoreCase);

        Lifestream.ExecuteCommand(command);
        return true;
    }

    public static bool TryStartReturnHomeWorld(out string? error)
    {
        error = null;
        var player = Dalamud.Objects.LocalPlayer;
        if (player == null)
        {
            error = "Cannot return to Home World: the local player is unavailable.";
            return false;
        }

        var homeWorld = player.HomeWorld.ValueNullable?.Name.ExtractText();
        if (string.IsNullOrWhiteSpace(homeWorld))
        {
            error = "Cannot return to Home World: Home World is unknown.";
            return false;
        }

        if (player.CurrentWorld.RowId == player.HomeWorld.RowId)
            return true;
        if (Dalamud.Conditions[ConditionFlag.BoundByDuty])
        {
            error = "Cannot return to Home World while bound by duty.";
            return false;
        }
        var group = GroupManager.Instance();
        if (!Dalamud.Conditions[ConditionFlag.ParticipatingInCrossWorldPartyOrAlliance]
            && group != null
            && group->MainGroup.MemberCount > 1)
        {
            error = "Cannot return to Home World while in a non-cross-world party.";
            return false;
        }
        if (!Lifestream.Enabled || Lifestream.TPAndChangeWorld == null)
        {
            error = "Lifestream is required to return to Home World.";
            return false;
        }
        if (Lifestream.CanVisitSameDC?.Invoke(homeWorld) != true)
        {
            error = $"Home World {homeWorld} is not reachable in the current data center.";
            return false;
        }
        if (Lifestream.IsBusy())
            return true;

        Lifestream.TPAndChangeWorld(homeWorld, false, string.Empty, false, null, true, true);
        return true;
    }

    public static bool TryStartInn(out string? error)
    {
        error = null;
        if (Dalamud.Conditions[ConditionFlag.BoundByDuty])
        {
            error = "Cannot go to an inn while bound by duty.";
            return false;
        }
        if (!Lifestream.Enabled)
        {
            error = "Lifestream is required to go to an inn.";
            return false;
        }
        if (Lifestream.IsBusy())
            return true;

        Lifestream.ExecuteCommand("inn");
        return true;
    }

    public static bool IsAtHomeWorld()
    {
        var player = Dalamud.Objects.LocalPlayer;
        return player != null
            && player.HomeWorld.RowId != 0
            && player.CurrentWorld.RowId == player.HomeWorld.RowId;
    }

    public static bool IsReturnComplete()
        => !Lifestream.Enabled || !Lifestream.IsBusy();
}
