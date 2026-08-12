using GatherBuddy.Plugin;
using Lumina.Excel.Sheets;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using System;
using Dalamud.Game.ClientState.Conditions;
using GatherBuddy.Automation;
using GatherBuddy.Helpers;
using GatherBuddy.Crafting;

namespace GatherBuddy.AutoGather;

public unsafe partial class AutoGather
{
    private string? _npcRepairFailure;

    private Item? EquipmentNeedingRepair()
    {
        const int defaultThreshold = 5;
        var threshold = GatherBuddy.Config.AutoGatherConfig.DoRepair ? GatherBuddy.Config.AutoGatherConfig.RepairThreshold : defaultThreshold;

        var equippedItems = InventoryManager.Instance()->GetInventoryContainer(InventoryType.EquippedItems);
        for (var i = 0; i < equippedItems->Size; i++)
        {
            var equippedItem = equippedItems->GetInventorySlot(i);
            if (equippedItem != null && equippedItem->ItemId > 0)
            {
                if (equippedItem->Condition / 300 <= threshold)
                {
                    return Dalamud.GameData.Excel.GetSheet<Item>().GetRow(equippedItem->ItemId);
                }
            }
        }

        return null;
    }

    private bool HasRepairJob(Item itemToRepair)
    {
        if (itemToRepair.ClassJobRepair.RowId > 0)
        {
            var repairJobLevel =
                PlayerState.Instance()->ClassJobLevels[
                    Dalamud.GameData.GetExcelSheet<ClassJob>()?.GetRow(itemToRepair.ClassJobRepair.RowId).ExpArrayIndex ?? 0];
            if (Math.Max(1, itemToRepair.LevelEquip - 10) <= repairJobLevel)
                return true;
        }

        return false;
    }

    private bool HasDarkMatter(Item itemToRepair)
    {
        var darkMatters = Dalamud.GameData.Excel.GetSheet<ItemRepairResource>();
        foreach (var darkMatter in darkMatters)
        {
            if (darkMatter.Item.RowId < itemToRepair.ItemRepair.Value.Item.RowId)
                continue;

            if (GetInventoryItemCount(darkMatter.Item.RowId) > 0)
                return true;
        }

        return false;
    }

    private bool RepairIfNeeded()
    {
        if (Dalamud.Conditions[ConditionFlag.Mounted] || Player.Job is not 16 /* MIN */ and not 17 /* BTN */ and not 18 /* FSH */)
            return false;

        var itemToRepair = EquipmentNeedingRepair();

        if (itemToRepair == null)
        {
            _npcRepairFailure = null;
            return false;
        }

        if (!GatherBuddy.Config.AutoGatherConfig.DoRepair)
        {
            Communicator.PrintError("Your gear is almost broken. Repair it before enabling Auto-Gather.");
            AbortAutoGather("Repairs needed.");
            return true;
        }

        if (_npcRepairFailure != null)
        {
            var failure = _npcRepairFailure;
            _npcRepairFailure = null;
            AbortAutoGather(failure);
            return true;
        }

        if (!HasRepairJob((Item)itemToRepair) || !HasDarkMatter((Item)itemToRepair))
        {
            if (TryQueueNPCRepair(false))
                return true;

            AbortAutoGather("Repairs needed, but neither self-repair nor an affordable reachable mender is available.");
            return true;
        }

        AutoStatus = "Repairing...";
        StopNavigation();

        var delay = (int)GatherBuddy.Config.AutoGatherConfig.ExecutionDelay;
        if (RepairAddon == null)
            ActionManager.Instance()->UseAction(ActionType.GeneralAction, 6);

        TaskManager.Enqueue(() => RepairAddon != null, 1000, true, "Wait until repair menu is ready.");
        TaskManager.DelayNext(delay);
        TaskManager.Enqueue(() => { if (RepairAddon is var addon && addon != null) { GatherBuddy.Log.Debug("[Repair] Clicking RepairAll button"); new AddonMaster.Repair(addon).RepairAll(); } }, 1000, "Repairing all.");
        TaskManager.Enqueue(() => SelectYesnoAddon != null, 1000, true, "Wait until YesnoAddon is ready.");
        TaskManager.DelayNext(delay);
        TaskManager.Enqueue(() => { if (SelectYesnoAddon is var addon && addon != null) Callback.Fire(&addon->AtkUnitBase, true, 0); }, 1000, "Confirm repairs.");
        TaskManager.Enqueue(() => !Dalamud.Conditions[ConditionFlag.Occupied39], 5000, "Wait for repairs.");
        TaskManager.DelayNext(delay);
        TaskManager.Enqueue(() => { if (RepairAddon is var addon and not null) Callback.Fire(&addon->AtkUnitBase, true, -1); }, 1000, true, "Close repair menu.");
        TaskManager.DelayNext(1000);
        TaskManager.Enqueue(() => {
            var repairAutoAddon = GetAddon<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>("RepairAuto");
            if (repairAutoAddon == null || !repairAutoAddon->IsVisible)
                return true;
            GatherBuddy.Log.Debug("[Repair] RepairAuto window still visible, closing it");
            repairAutoAddon->Close(true);
            return true;
        }, 3000, "Wait for RepairAuto window to close or force close it.");
        TaskManager.DelayNext(delay);

        return true;
    }

    private DateTime _lastRepairTime = DateTime.MinValue;
    
    private bool RepairIfNeededForFishing()
    {
        if (Dalamud.Conditions[ConditionFlag.Mounted] || Player.Job is not 18 /* FSH */)
            return false;

        var itemToRepair = EquipmentNeedingRepair();

        if (itemToRepair == null)
        {
            _npcRepairFailure = null;
            _lastRepairTime = DateTime.MinValue;
            return false;
        }
        
        if (GatherBuddy.Config.AutoGatherConfig.DeferRepairDuringFishingBuffs && (IsFishing || HasActiveFishingBuff()))
            return false;
        
        if ((DateTime.Now - _lastRepairTime).TotalSeconds < 5)
            return false;

        if (!GatherBuddy.Config.AutoGatherConfig.DoRepair)
        {
            Communicator.PrintError("Your gear is almost broken. Repair it before enabling Auto-Gather.");
            AbortAutoGather("Repairs needed.");
            return true;
        }

        if (_npcRepairFailure != null)
        {
            var failure = _npcRepairFailure;
            _npcRepairFailure = null;
            AbortAutoGather(failure);
            return true;
        }

        if (!HasRepairJob((Item)itemToRepair) || !HasDarkMatter((Item)itemToRepair))
        {
            if (TryQueueNPCRepair(true))
                return true;

            AbortAutoGather("Repairs needed, but neither self-repair nor an affordable reachable mender is available.");
            return true;
        }

        AutoStatus = "Repairing...";
        _lastRepairTime = DateTime.Now;
        var delay = (int)GatherBuddy.Config.AutoGatherConfig.ExecutionDelay;
        
        TaskManager.Enqueue(StopNavigation);
        
        if (GatherBuddy.Config.AutoGatherConfig.UseAutoHook && AutoHook.Enabled)
        {
            TaskManager.Enqueue(() =>
            {
                AutoHook.SetPluginState?.Invoke(false);
                AutoHook.SetAutoStartFishing?.Invoke(false);
            });
        }
        
        if (IsGathering || IsFishing)
        {
            QueueQuitFishingTasks();
            TaskManager.Enqueue(() => !IsFishing, 5000, "Wait until fishing stopped.");
        }
        
        if (RepairAddon == null)
        {
            EnqueueActionWithDelay(() => ActionManager.Instance()->UseAction(ActionType.GeneralAction, 6));
        }

        TaskManager.Enqueue(() => RepairAddon != null, 1000, true, "Wait until repair menu is ready.");
        TaskManager.DelayNext(delay);
        TaskManager.Enqueue(() => { if (RepairAddon is var addon && addon != null) new AddonMaster.Repair(addon).RepairAll(); }, 1000, "Repairing all.");
        TaskManager.Enqueue(() => SelectYesnoAddon != null, 1000, true, "Wait until YesnoAddon is ready.");
        TaskManager.DelayNext(delay);
        TaskManager.Enqueue(() => { if (SelectYesnoAddon is var addon && addon != null) new AddonMaster.SelectYesno(addon).Yes(); }, 1000, "Confirm repairs.");
        TaskManager.Enqueue(() => !Dalamud.Conditions[ConditionFlag.Occupied39], 5000, "Wait for repairs.");
        TaskManager.DelayNext(delay);
        TaskManager.Enqueue(() => { if (RepairAddon is var addon and not null) Callback.Fire(&addon->AtkUnitBase, true, -1); }, 1000, true, "Close repair menu.");
        TaskManager.DelayNext(1000);
        TaskManager.Enqueue(() => {
            var repairAutoAddon = GetAddon<FFXIVClientStructs.FFXIV.Component.GUI.AtkUnitBase>("RepairAuto");
            if (repairAutoAddon == null || !repairAutoAddon->IsVisible)
                return true;
            GatherBuddy.Log.Debug("[Repair] RepairAuto window still visible, closing it");
            repairAutoAddon->Close(true);
            return true;
        }, 3000, "Wait for RepairAuto window to close or force close it.");
        TaskManager.DelayNext(delay);
        TaskManager.Enqueue(() =>
        {
            if (GatherBuddy.Config.AutoGatherConfig.UseAutoHook && AutoHook.Enabled)
            {
                AutoHook.SetPluginState?.Invoke(true);
                AutoHook.SetAutoStartFishing?.Invoke(true);
            }
        });

        return true;
    }

    private bool TryQueueNPCRepair(bool fishing)
    {
        var route = RepairNPCHelper.FindBestRepairRoute();
        if (!route.HasValue)
            return false;

        var repairPrice = Crafting.RepairManager.GetNPCRepairPrice();
        var gilCount = InventoryManager.Instance()->GetInventoryItemCount(1);
        var totalCost = (ulong)repairPrice + route.Value.TeleportCost;
        if ((ulong)gilCount < totalCost)
        {
            GatherBuddy.Log.Warning($"[AutoGather] Cannot afford NPC repair and travel ({gilCount}/{totalCost} gil)");
            return false;
        }

        AutoStatus = $"Navigating to mender {route.Value.NPC.Name}...";
        _lastRepairTime = DateTime.Now;
        CraftingTasks.ResetRepairState();
        TaskManager.Enqueue(StopNavigation);

        var restoreAutoHook = fishing && GatherBuddy.Config.AutoGatherConfig.UseAutoHook && AutoHook.Enabled;
        if (restoreAutoHook)
        {
            TaskManager.Enqueue(() =>
            {
                AutoHook.SetPluginState?.Invoke(false);
                AutoHook.SetAutoStartFishing?.Invoke(false);
            });
        }

        if (IsGathering || IsFishing)
        {
            QueueQuitFishingTasks();
            TaskManager.Enqueue(() => !IsFishing && !IsGathering, 5000, "Wait until gathering stopped for NPC repair");
        }

        TaskManager.Enqueue(
            CreateNPCRepairTask(
                () => CraftingTasks.TaskNavigateToRepairNPC(route.Value.NPC, route.Value.AetheryteId),
                69000,
                "navigation to the mender",
                restoreAutoHook),
            70000,
            true,
            "Navigate to repair NPC");
        TaskManager.Enqueue(CreateNPCRepairTask(CraftingTasks.TaskInteractWithRepairNPC, 9000, "interaction with the mender", restoreAutoHook), 10000, true, "Interact with repair NPC");
        TaskManager.Enqueue(CreateNPCRepairTask(CraftingTasks.TaskSelectRepairFromMenu, 29000, "opening the repair menu", restoreAutoHook), 30000, true, "Select NPC repair");
        TaskManager.Enqueue(CreateNPCRepairTask(() => CraftingTasks.TaskExecuteRepair(), 29000, "executing the repair", restoreAutoHook), 30000, true, "Execute NPC repair");
        TaskManager.Enqueue(CreateNPCRepairTask(CraftingTasks.TaskCloseRepairWindow, 14000, "closing the repair window", restoreAutoHook), 15000, true, "Close NPC repair window");

        if (restoreAutoHook)
        {
            TaskManager.Enqueue(RestoreAutoHookAfterRepair);
        }

        GatherBuddy.Log.Information(
            $"[AutoGather] Falling back to mender {route.Value.NPC.Name} " +
            $"(repair: {repairPrice} gil, teleport: {route.Value.TeleportCost} gil)");
        return true;
    }

    private Func<bool?> CreateNPCRepairTask(
        Func<CraftingTasks.TaskResult> task,
        int timeoutMilliseconds,
        string operation,
        bool restoreAutoHook)
    {
        long? deadline = null;
        return () =>
        {
            deadline ??= Environment.TickCount64 + timeoutMilliseconds;
            if (Environment.TickCount64 > deadline)
                return FailNPCRepair($"NPC repair timed out during {operation}.", restoreAutoHook);

            return task() switch
            {
                CraftingTasks.TaskResult.Done => true,
                CraftingTasks.TaskResult.Retry => false,
                _ => FailNPCRepair($"NPC repair failed during {operation}.", restoreAutoHook),
            };
        };
    }

    private bool? FailNPCRepair(string failure, bool restoreAutoHook)
    {
        CraftingTasks.ResetRepairState();
        if (restoreAutoHook)
            RestoreAutoHookAfterRepair();
        _npcRepairFailure = failure;
        return null;
    }

    private void RestoreAutoHookAfterRepair()
    {
        AutoHook.SetPluginState?.Invoke(true);
        AutoHook.SetAutoStartFishing?.Invoke(true);
    }
}
