using System;

namespace GatherBuddy.Vulcan;

internal static class StepStateReconciler
{
    internal static bool TryReconcileExternalAction(
        CraftState craft,
        StepState previous,
        StepState observed,
        out StepState reconciled)
    {
        if (ObservableEquivalent(previous, observed))
        {
            reconciled = OverlayObserved(previous, observed);
            return true;
        }

        StepState? unique = null;
        foreach (var action in Enum.GetValues<VulcanSkill>())
        {
            if (action is VulcanSkill.None or VulcanSkill.TouchCombo or VulcanSkill.TouchComboRefined)
                continue;

            var successRate = Simulator.GetSuccessRate(previous, action);
            foreach (var successRoll in successRate < 1.0f ? new[] { 0.0f, 1.0f } : new[] { 0.0f })
            {
                var (result, candidate) = Simulator.Execute(craft, previous, action, successRoll, 0.5f);
                if (result == Simulator.ExecuteResult.CantUse || !ActionOutcomeEquivalent(candidate, observed))
                    continue;

                candidate = OverlayObserved(candidate, observed);
                if (unique != null && !PersistentEquivalent(unique, candidate))
                {
                    reconciled = observed;
                    return false;
                }

                unique = candidate;
            }
        }

        reconciled = unique ?? observed;
        return unique != null;
    }

    internal static bool TryReconcileAction(
        CraftState craft,
        StepState previous,
        VulcanSkill action,
        StepState observed,
        out StepState reconciled)
    {
        StepState? unique = null;
        var successRate = Simulator.GetSuccessRate(previous, action);
        foreach (var successRoll in successRate < 1.0f ? new[] { 0.0f, 1.0f } : new[] { 0.0f })
        {
            var (result, candidate) = Simulator.Execute(craft, previous, action, successRoll, 0.5f);
            if (result == Simulator.ExecuteResult.CantUse || !ActionOutcomeEquivalent(candidate, observed))
                continue;

            candidate = OverlayObserved(candidate, observed);
            if (unique != null && !PersistentEquivalent(unique, candidate))
            {
                reconciled = observed;
                return false;
            }

            unique = candidate;
        }

        reconciled = unique ?? observed;
        return unique != null;
    }

    internal static bool ObservableEquivalent(StepState left, StepState right)
        => left.Condition == right.Condition && ActionOutcomeEquivalent(left, right);

    private static bool ActionOutcomeEquivalent(StepState left, StepState right)
        => left.Index == right.Index
            && left.Progress == right.Progress
            && left.Quality == right.Quality
            && left.Durability == right.Durability
            && left.RemainingCP == right.RemainingCP
            && left.IQStacks == right.IQStacks
            && left.WasteNotLeft == right.WasteNotLeft
            && left.ManipulationLeft == right.ManipulationLeft
            && left.GreatStridesLeft == right.GreatStridesLeft
            && left.InnovationLeft == right.InnovationLeft
            && left.VenerationLeft == right.VenerationLeft
            && left.MuscleMemoryLeft == right.MuscleMemoryLeft
            && left.FinalAppraisalLeft == right.FinalAppraisalLeft
            && left.HeartAndSoulActive == right.HeartAndSoulActive
            && left.ExpedienceLeft == right.ExpedienceLeft
            && left.TrainedPerfectionActive == right.TrainedPerfectionActive
            && left.MaterialMiracleActive == right.MaterialMiracleActive
            && left.StellarSteadyHandLeft == right.StellarSteadyHandLeft;

    private static bool PersistentEquivalent(StepState left, StepState right)
        => left.CarefulObservationLeft == right.CarefulObservationLeft
            && left.CrafterDelineationsLeft == right.CrafterDelineationsLeft
            && left.HeartAndSoulAvailable == right.HeartAndSoulAvailable
            && left.PrevActionFailed == right.PrevActionFailed
            && left.QuickInnoLeft == right.QuickInnoLeft
            && left.QuickInnoAvailable == right.QuickInnoAvailable
            && left.TrainedPerfectionAvailable == right.TrainedPerfectionAvailable
            && left.PrevComboAction == right.PrevComboAction
            && left.MaterialMiracleCharges == right.MaterialMiracleCharges
            && left.MaterialMiraclesUsed == right.MaterialMiraclesUsed
            && left.StellarSteadyHandCharges == right.StellarSteadyHandCharges
            && left.StellarSteadyHandsUsed == right.StellarSteadyHandsUsed
            && left.ObserveCounter == right.ObserveCounter;

    private static StepState OverlayObserved(StepState inferred, StepState observed)
    {
        var result = inferred with { };
        result.Index = observed.Index;
        result.Progress = observed.Progress;
        result.Quality = observed.Quality;
        result.Durability = observed.Durability;
        result.RemainingCP = observed.RemainingCP;
        result.Condition = observed.Condition;
        result.IQStacks = observed.IQStacks;
        result.WasteNotLeft = observed.WasteNotLeft;
        result.ManipulationLeft = observed.ManipulationLeft;
        result.GreatStridesLeft = observed.GreatStridesLeft;
        result.InnovationLeft = observed.InnovationLeft;
        result.VenerationLeft = observed.VenerationLeft;
        result.MuscleMemoryLeft = observed.MuscleMemoryLeft;
        result.FinalAppraisalLeft = observed.FinalAppraisalLeft;
        result.HeartAndSoulActive = observed.HeartAndSoulActive;
        result.ExpedienceLeft = observed.ExpedienceLeft;
        result.TrainedPerfectionActive = observed.TrainedPerfectionActive;
        result.MaterialMiracleActive = observed.MaterialMiracleActive;
        result.StellarSteadyHandLeft = observed.StellarSteadyHandLeft;
        return result;
    }
}
