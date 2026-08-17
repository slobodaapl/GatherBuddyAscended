using System;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using GatherBuddy.Plugin;
using Dalamud.Game.ClientState.Conditions;
using GatherBuddy.Automation;
using GatherBuddy.AutoGather.Collectables;
using PurifyResult = GatherBuddy.Automation.AddonMaster.PurifyResult;

namespace GatherBuddy.AutoGather
{
    public partial class AutoGather
    {
        private bool HasReducibleItems(bool requireConfigured = true)
        {
            if (requireConfigured && !GatherBuddy.Config.AutoGatherConfig.DoReduce
             || Dalamud.Conditions[ConditionFlag.Mounted])
                return false;

            if (!QuestManager.IsQuestComplete(67633))
            {
                if (requireConfigured
                 && !_autoRetainerMultiModeEnabled
                 && string.IsNullOrEmpty(_originalCharacterNameWorld))
                {
                    GatherBuddy.Config.AutoGatherConfig.DoReduce = false;
                    Communicator.PrintError(
                        "[GatherBuddy Ascended] Aetherial reduction is enabled, but the relevant quest has not been completed yet. The feature has been disabled.");
                }
                GatherBuddy.Log.Debug($"[Reduction] Skipping reduction - quest not complete. AR MultiMode: {_autoRetainerMultiModeEnabled}, Original Character: {_originalCharacterNameWorld ?? "null"}");
                return false;
            }

            var items = ItemHelper.GetCurrentInventoryItems();
            foreach (var item in items)
            {
                if (!item.IsCollectable)
                    continue;

                // Check regular gatherables
                if (GatherBuddy.GameData.Gatherables.TryGetValue(item.BaseItemId, out var gatherable)
                 && gatherable.ItemData.AetherialReduce != 0)
                {
                    return true;
                }
                
                // Check fish
                if (GatherBuddy.GameData.Fishes.TryGetValue(item.BaseItemId, out var fish)
                 && fish.ItemData.AetherialReduce != 0)
                {
                    return true;
                }
            }

            return false;
        }

        private unsafe void ReduceItems(bool reduceAll, Action? onComplete = null, uint sourceItemId = 0)
        {
            AutoStatus = "Aetherial reduction";
            var delay = (int)GatherBuddy.Config.AutoGatherConfig.ExecutionDelay;
            TaskManager.Enqueue(StopNavigation);
            var agent = AgentPurify.Instance();
            if (agent == null || !agent->IsAgentActive())
            {
                EnqueueActionWithDelay(() => { ActionManager.Instance()->UseAction(ActionType.GeneralAction, 21); });
                // Prevent the "Unable to execute command while occupied" message right after entering a house.
                TaskManager.DelayNext(500);
            }

            TaskManager.Enqueue(() => ReduceFirstItem(sourceItemId),             3000, true, "Reduce selected item");
            TaskManager.Enqueue(() => !Dalamud.Conditions[ConditionFlag.Occupied39], 5000, true, "Wait until first item reduction is complete");
            TaskManager.DelayNext(delay);
            TaskManager.Enqueue(StartAutoReduction,                             1000, true, "Start auto reduction");
            TaskManager.Enqueue(() => !Dalamud.Conditions[ConditionFlag.Occupied39], 180000, true, "Wait until all items have been reduced");
            TaskManager.DelayNext(delay);
            TaskManager.Enqueue(() =>
            {
                EnqueueActionWithDelay(() =>
                {
                    if (PurifyResultAddon is var addon and not null)
                        Callback.Fire(addon, true, -1);
                });
                if (reduceAll && HasReducibleItems())
                    ReduceItems(true, onComplete);
                else
                {
                    EnqueueActionWithDelay(() =>
                    {
                        if (PurifyItemSelectorAddon is var addon and not null)
                            Callback.Fire(addon, true, -1);
                        else if (AgentPurify.Instance() is var agent and not null && agent->IsAgentActive())
                            agent->Hide();
                    });
                    if (onComplete != null)
                        TaskManager.Enqueue(() => onComplete());
                }
            });
        }

        private unsafe bool? ReduceFirstItem(uint sourceItemId)
        {
            var agent = AgentPurify.Instance();
            var inventory = InventoryManager.Instance();
            if (agent == null || inventory == null || !agent->IsAgentActive())
                return false;

            foreach (var inventoryType in InventoryTypes)
            {
                var container = inventory->GetInventoryContainer(inventoryType);
                if (container == null || !container->IsLoaded)
                    continue;

                for (var slot = 0; slot < container->Size; ++slot)
                {
                    var inventoryItem = container->GetInventorySlot(slot);
                    if (inventoryItem == null || inventoryItem->ItemId == 0 || !inventoryItem->IsCollectable())
                        continue;

                    var itemId = inventoryItem->GetBaseItemId();
                    if (sourceItemId != 0 && itemId != sourceItemId)
                        continue;

                    var reducible = GatherBuddy.GameData.Gatherables.TryGetValue(itemId, out var gatherable)
                        ? gatherable.ItemData.AetherialReduce != 0
                        : GatherBuddy.GameData.Fishes.TryGetValue(itemId, out var fish)
                          && fish.ItemData.AetherialReduce != 0;
                    if (!reducible)
                        continue;

                    GatherBuddy.Log.Debug(
                        $"[Reduction] Reducing source item {itemId} from {inventoryType} slot {slot}.");
                    agent->ReduceItem(inventoryItem);
                    return true;
                }
            }

            return false;
        }

        private unsafe bool? StartAutoReduction()
        {
            var addon = PurifyResultAddon;
            if (addon == null)
                return false;

            new PurifyResult(addon).Automatic();
            return true;
        }
    }
}
