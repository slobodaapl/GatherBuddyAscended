using System;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GatherBuddy.Vulcan;

namespace GatherBuddy.Crafting;

public static unsafe class SynthesisReader
{
    internal readonly record struct LiveCraftMetrics(
        int MaxProgress,
        int MaxQuality,
        int MaxDurability);

    public static AtkUnitBase* GetSynthesisAddon()
    {
        var addon = Dalamud.GameGui.GetAddonByName("Synthesis");
        if (addon == null || addon.Address == nint.Zero)
            return null;
        
        return (AtkUnitBase*)addon.Address;
    }
    
    public static bool IsSynthesisWindowOpen()
    {
        var addon = GetSynthesisAddon();
        return addon != null && addon->IsVisible;
    }
    
    public static int GetStepIndex(AtkUnitBase* synthWindow)
    {
        if (synthWindow == null || synthWindow->AtkValuesCount < 16)
            return 0;
        return synthWindow->AtkValues[15].Int;
    }
    
    public static int GetProgress(AtkUnitBase* synthWindow)
    {
        if (synthWindow == null || synthWindow->AtkValuesCount < 6)
            return 0;
        return synthWindow->AtkValues[5].Int;
    }

    public static int GetMaxProgress(AtkUnitBase* synthWindow)
    {
        if (synthWindow == null || synthWindow->AtkValuesCount < 7)
            return 0;
        return synthWindow->AtkValues[6].Int;
    }
    
    public static int GetQuality(AtkUnitBase* synthWindow)
    {
        if (synthWindow == null || synthWindow->AtkValuesCount < 10)
            return 0;
        return synthWindow->AtkValues[9].Int;
    }

    public static int GetMaxQuality(AtkUnitBase* synthWindow)
    {
        if (synthWindow == null || synthWindow->AtkValuesCount < 18)
            return 0;
        return synthWindow->AtkValues[17].Int;
    }
    
    public static int GetDurability(AtkUnitBase* synthWindow)
    {
        if (synthWindow == null || synthWindow->AtkValuesCount < 8)
            return 0;
        return synthWindow->AtkValues[7].Int;
    }

    public static int GetMaxDurability(AtkUnitBase* synthWindow)
    {
        if (synthWindow == null || synthWindow->AtkValuesCount < 9)
            return 0;
        return synthWindow->AtkValues[8].Int;
    }

    public static bool MatchesCraft(CraftState craft)
    {
        return TryReadLiveCraftMetrics(out var metrics)
            && MatchesCraftMetrics(
                craft,
                metrics.MaxProgress,
                metrics.MaxQuality,
                metrics.MaxDurability);
    }

    internal static bool TryReadLiveCraftMetrics(out LiveCraftMetrics metrics)
    {
        var synthWindow = GetSynthesisAddon();
        if (synthWindow == null || !synthWindow->IsVisible || synthWindow->AtkValuesCount < 18)
        {
            metrics = default;
            return false;
        }

        Span<int> values = stackalloc int[18];
        for (var index = 0; index < values.Length; ++index)
            values[index] = synthWindow->AtkValues[index].Int;
        metrics = ReadLiveCraftMetrics(values);
        return metrics.MaxProgress > 0
            && metrics.MaxQuality >= 0
            && metrics.MaxDurability > 0;
    }

    internal static LiveCraftMetrics ReadLiveCraftMetrics(ReadOnlySpan<int> values)
        => new(
            values[6],
            values[17],
            values[8]);

    internal static CraftState ApplyLiveCraftMetrics(CraftState craft, LiveCraftMetrics metrics)
        => craft with
        {
            CraftProgress = metrics.MaxProgress,
            CraftQualityMax = metrics.MaxQuality,
            CraftDurability = metrics.MaxDurability,
        };

    internal static bool MatchesCraftMetrics(
        CraftState craft,
        int maxProgress,
        int maxQuality,
        int maxDurability)
        => maxProgress == craft.CraftProgress
            && maxQuality == craft.CraftQualityMax
            && maxDurability == craft.CraftDurability;
    
    public static Condition GetCondition(AtkUnitBase* synthWindow)
    {
        if (synthWindow == null || synthWindow->AtkValuesCount < 13)
            return Condition.Normal;
        return (Condition)synthWindow->AtkValues[12].Int;
    }
    
    public static StepState? ReadCurrentStepState(CraftState craft, StepState? previousStep = null)
    {
        var synthWindow = GetSynthesisAddon();
        if (synthWindow == null || !synthWindow->IsVisible || synthWindow->AtkValuesCount < 16)
            return null;
        var player = Dalamud.Objects.LocalPlayer;
        if (player == null)
            return null;
        
        var step = new StepState
        {
            Index = GetStepIndex(synthWindow),
            Progress = GetProgress(synthWindow),
            Quality = Simulator.ClampQuality(craft, GetQuality(synthWindow)),
            Durability = GetDurability(synthWindow),
            RemainingCP = (int)player.CurrentCp,
            Condition = GetCondition(synthWindow),
            TrainedPerfectionAvailable = previousStep?.TrainedPerfectionAvailable
                ?? craft.StatLevel >= Simulator.MinLevel(VulcanSkill.TrainedPerfection),
            HeartAndSoulAvailable = previousStep?.HeartAndSoulAvailable ?? craft.Specialist,
            QuickInnoAvailable = previousStep?.QuickInnoAvailable ?? craft.Specialist,
            MaterialMiracleCharges = craft.MissionHasMaterialMiracle
                ? CraftingStateBuilder.GetDutyActionCharges((uint)VulcanSkill.MaterialMiracle)
                : 0,
            StellarSteadyHandCharges = craft.MissionHasStellarSteadyHand
                ? CraftingStateBuilder.GetDutyActionCharges((uint)VulcanSkill.StellarSteadyHand)
                : 0,
            StellarSteadyHandsUsed = previousStep?.StellarSteadyHandsUsed ?? 0,
            CarefulObservationLeft = previousStep?.CarefulObservationLeft ?? (craft.Specialist ? 3 : 0),
            CrafterDelineationsLeft = previousStep?.CrafterDelineationsLeft ?? craft.CrafterDelineations,
            QuickInnoLeft = previousStep?.QuickInnoLeft ?? (craft.Specialist ? 1 : 0),
            // Crafting combo state is action-derived; reconciliation advances it from each inferred outcome.
            ComboAction = previousStep?.ComboAction ?? VulcanSkill.None,
            PrevComboAction = previousStep?.PrevComboAction ?? VulcanSkill.None,
            PrevActionFailed = previousStep?.PrevActionFailed ?? false
        };
        
        ReadBuffsIntoStepState(step);
        
        if (step.TrainedPerfectionActive)
            step.TrainedPerfectionAvailable = false;
        
        return step;
    }

    private static void ReadBuffsIntoStepState(StepState step)
    {
        var player = Dalamud.Objects.LocalPlayer;
        if (player == null)
            return;
        
        foreach (var status in player.StatusList)
        {
            switch (status.StatusId)
            {
                case 251:
                    step.IQStacks = status.Param;
                    break;
                case 252:
                    step.WasteNotLeft = status.Param;
                    break;
                case 257:
                    step.WasteNotLeft = status.Param;
                    break;
                case 1164:
                    step.ManipulationLeft = status.Param;
                    break;
                case 254:
                    step.GreatStridesLeft = status.Param;
                    break;
                case 2189:
                    step.InnovationLeft = status.Param;
                    break;
                case 2226:
                    step.VenerationLeft = status.Param;
                    break;
                case 2191:
                    step.MuscleMemoryLeft = status.Param;
                    break;
                case 2190:
                    step.FinalAppraisalLeft = status.Param;
                    break;
                case 2665:
                    step.HeartAndSoulActive = true;
                    break;
                case 3813:
                    step.TrainedPerfectionActive = true;
                    break;
                case 3812:
                    step.ExpedienceLeft = status.Param;
                    break;
                case 4839:
                    step.StellarSteadyHandLeft = status.Param;
                    break;
            }
        }
    }
}
