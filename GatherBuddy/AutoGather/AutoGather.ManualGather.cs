using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using GatherBuddy.AutoGather.AtkReaders;
using GatherBuddy.AutoGather.Lists;
using GatherBuddy.Classes;
using GatherBuddy.Plugin;
using GatherBuddy.Time;
using System;
using System.Linq;

namespace GatherBuddy.AutoGather;

internal static class ManualGatherAssistPolicy
{
    public static bool IsEnabled(AutoGatherConfig config)
        => config.DoGathering && config.AssistManualGathering;
}

public partial class AutoGather
{
    private const uint ManualGatherQuantity = int.MaxValue;

    private GatherTarget? _manualGatherTarget;
    private GatherTarget? _pendingManualCollectableTarget;
    private int _pendingManualCollectableGatherChance;
    private int? _manualObservedIntegrity;

    private unsafe void OnManualGatheringFinalize(AddonEvent type, AddonArgs args)
    {
        if (Enabled || !ManualGatherAssistPolicy.IsEnabled(GatherBuddy.Config.AutoGatherConfig))
            return;

        var reader = new GatheringReader(
            (FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase*)args.Addon.Address);
        var slotIndex = reader.LastSelectedSlotIndex;
        if (slotIndex < 0 || slotIndex >= reader.ItemSlots.Length)
            return;

        var slot = reader.ItemSlots[slotIndex];
        if (slot.IsEmpty || !slot.IsCollectable)
            return;

        var target = CreateManualGatherTarget(slot.Item);
        if (target == null)
            return;

        _pendingManualCollectableTarget = target;
        _pendingManualCollectableGatherChance = slot.GatherChance;
        _manualObservedIntegrity = reader.IntegrityRemaining;
    }

    private void DoManualGatherAssist()
    {
        var config = GatherBuddy.Config.AutoGatherConfig;
        if (!ManualGatherAssistPolicy.IsEnabled(config))
        {
            ResetManualGatherAssist();
            return;
        }

        if (FreeInventorySlots == 0)
        {
            StopManualGatherTarget();
            return;
        }

        var masterpiece = MasterpieceReader;
        if (masterpiece?.IsValid == true)
        {
            if (_manualGatherTarget == null && _pendingManualCollectableTarget is { } pendingTarget)
            {
                _manualGatherTarget = pendingTarget;
                _pendingManualCollectableTarget = null;
                CurrentCollectableRotation = new CollectableRotation(
                    MatchConfigPreset(pendingTarget.Gatherable!),
                    pendingTarget.Gatherable!,
                    ManualGatherQuantity,
                    _pendingManualCollectableGatherChance);
            }

            if (_manualGatherTarget is not { } collectableTarget
             || collectableTarget.Gatherable == null
             || !ManualGatherAssistPolicy.IsEnabled(config)
             || TaskManager.IsBusy
             || !CanAct)
                return;

            RunManualGatherAction(collectableTarget);
            return;
        }

        var reader = GatheringWindowReader;
        if (reader == null)
        {
            if (!IsGathering)
                ResetManualGatherAssist();
            return;
        }

        if (reader.QuickGatheringInProgress)
        {
            _manualObservedIntegrity = reader.IntegrityRemaining;
            StopManualGatherTarget();
            return;
        }

        _manualObservedIntegrity ??= reader.IntegrityMax;
        if (_manualGatherTarget == null && reader.IntegrityRemaining < _manualObservedIntegrity.Value)
        {
            _manualObservedIntegrity = reader.IntegrityRemaining;
            TryBeginManualGatherTarget(reader);
        }
        else if (reader.IntegrityRemaining > _manualObservedIntegrity.Value)
        {
            _manualObservedIntegrity = reader.IntegrityRemaining;
        }

        if (_manualGatherTarget is not { } target
         || target.Gatherable == null
         || !ManualGatherAssistPolicy.IsEnabled(config)
         || TaskManager.IsBusy
         || !CanAct)
            return;

        RunManualGatherAction(target);
    }

    private void TryBeginManualGatherTarget(GatheringReader reader)
    {
        var slotIndex = reader.LastSelectedSlotIndex;
        if (slotIndex < 0 || slotIndex >= reader.ItemSlots.Length)
            return;

        var slot = reader.ItemSlots[slotIndex];
        if (slot.IsEmpty || slot.IsCollectable
         || !ManualGatherAssistPolicy.IsEnabled(GatherBuddy.Config.AutoGatherConfig))
            return;

        _manualGatherTarget = CreateManualGatherTarget(slot.Item);
        if (_manualGatherTarget != null)
            GatherBuddy.Log.Debug($"[AutoGather] Continuing manually selected item {slot.Item.ItemId}.");
    }

    private void RunManualGatherAction(GatherTarget target)
    {
        try
        {
            AutoStatus = "Continuing manually selected item...";
            DoActionTasks(target, selectedTargetOnly: true);
        }
        catch (NoGatherableItemsInNodeException)
        {
            // Selected one-shot items (for example treasure maps) may disappear
            // while the node still has integrity. Stop assistance; never close
            // the manually opened node or substitute another item.
            StopManualGatherTarget();
        }
        catch (NoCollectableActionsException)
        {
            Communicator.PrintError(
                "Unable to pick a collectability increasing action. Manual gathering assistance stopped.");
            StopManualGatherTarget();
        }
        catch (CollectableSolverException exception)
        {
            GatherBuddy.Log.Error($"[AutoGather] Manual collectable solver failed: {exception}");
            Communicator.PrintError(
                "The collectable solver failed. Manual gathering assistance stopped before issuing an unsafe action.");
            StopManualGatherTarget();
        }
    }

    private GatherTarget? CreateManualGatherTarget(Gatherable item)
    {
        var targetNodeId = (Dalamud.Targets.Target ?? Dalamud.Targets.PreviousTarget)?.BaseId ?? 0;
        var node = item.NodeList.FirstOrDefault(candidate => candidate.WorldPositions.ContainsKey(targetNodeId))
                ?? item.NodeList.FirstOrDefault();
        return node == null
            ? null
            : new GatherTarget(item, node, TimeInterval.Always, ManualGatherQuantity);
    }

    private void StopManualGatherTarget()
    {
        var wasManualCollectable = _manualGatherTarget?.Gatherable?.ItemData.IsCollectable == true;
        _manualGatherTarget = null;
        _pendingManualCollectableTarget = null;
        _pendingManualCollectableGatherChance = 0;
        ActionSequence = null;
        if (wasManualCollectable)
            CurrentCollectableRotation = null;
    }

    private void ResetManualGatherAssist()
    {
        if (_manualGatherTarget != null
         || _pendingManualCollectableTarget != null
         || _manualObservedIntegrity != null)
            StopManualGatherTarget();
        _manualObservedIntegrity = null;
    }
}
