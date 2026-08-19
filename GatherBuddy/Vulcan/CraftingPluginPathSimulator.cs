using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using GatherBuddy.Crafting;

namespace GatherBuddy.Vulcan;

internal sealed record PluginPathSimulationScenario(
    ulong GameSeed,
    IReadOnlyDictionary<int, Condition>? ForcedConditions = null,
    IReadOnlyDictionary<int, VulcanSkill>? ManualActions = null,
    bool IsTrial = false);

internal sealed record PluginPathSimulationTraceEntry(
    int ActionNumber,
    VulcanSkill RecommendedAction,
    VulcanSkill ExecutedAction,
    bool Manual,
    Condition PreviousCondition,
    Condition RolledCondition,
    Condition ResultCondition,
    Simulator.ExecuteResult ExecuteResult,
    StepState State);

internal sealed record PluginPathSimulationResult(
    StepState FinalState,
    IReadOnlyList<PluginPathSimulationTraceEntry> Trace,
    bool SynthesisCompleted,
    bool FullQuality,
    bool SolverTerminalFailure,
    string? FailureReason);

internal sealed record GabrielPluginPathEstimate(
    int Successes,
    int Samples,
    int SynthesisCompletions,
    int DurabilityFailures,
    int SolverTerminalFailures,
    int MinFinalQuality,
    double AverageFinalQuality,
    int MaxFinalQuality,
    IReadOnlyDictionary<string, int> TerminalFailureReasons,
    double Probability,
    long ElapsedMillis);

internal static class CraftingPluginPathSimulator
{
    private const int MaximumActions = 100;
    private static readonly TimeSpan RecommendationTimeout = TimeSpan.FromSeconds(30);

    internal static GabrielPluginPathEstimate EstimateGabriel(
        CraftState craft,
        StepState root,
        int samples,
        ulong seed,
        Func<bool>? cancellationRequested = null)
    {
        if (samples <= 0)
            throw new ArgumentOutOfRangeException(nameof(samples));
        if (!GabrielPolicyCatalog.TryResolve(craft, out _, out var reason))
            throw new InvalidOperationException(reason);

        var started = Stopwatch.StartNew();
        var successes = 0;
        var completions = 0;
        var durabilityFailures = 0;
        var terminalFailures = 0;
        var minFinalQuality = int.MaxValue;
        var maxFinalQuality = 0;
        long totalFinalQuality = 0;
        var terminalFailureReasons = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var sample = 0; sample < samples; ++sample)
        {
            ThrowIfCancelled(cancellationRequested);
            var sampleSeed = Mix(seed, (ulong)(uint)sample);
            var result = Run(
                craft,
                root,
                new GabrielSolverDefinition(Mix(sampleSeed, 0xA076_1D64_78BD_642F)),
                VulcanSolverMode.Gabriel,
                new PluginPathSimulationScenario(Mix(sampleSeed, 0xE703_7ED1_A0B4_28DB)),
                cancellationRequested);
            if (result.FailureReason != null && !result.SolverTerminalFailure)
                throw new InvalidOperationException(result.FailureReason);
            if (result.FullQuality)
                successes++;
            if (result.SynthesisCompleted)
                completions++;
            if (!result.SynthesisCompleted && result.FinalState.Durability <= 0)
                durabilityFailures++;
            if (result.SolverTerminalFailure)
            {
                terminalFailures++;
                var failureReason = result.FailureReason ?? "Unknown terminal failure";
                terminalFailureReasons[failureReason] = terminalFailureReasons.GetValueOrDefault(failureReason) + 1;
            }
            minFinalQuality = Math.Min(minFinalQuality, result.FinalState.Quality);
            maxFinalQuality = Math.Max(maxFinalQuality, result.FinalState.Quality);
            totalFinalQuality += result.FinalState.Quality;
        }

        return new(
            successes,
            samples,
            completions,
            durabilityFailures,
            terminalFailures,
            minFinalQuality == int.MaxValue ? 0 : minFinalQuality,
            (double)totalFinalQuality / samples,
            maxFinalQuality,
            terminalFailureReasons,
            (double)successes / samples,
            started.ElapsedMilliseconds);
    }

    internal static PluginPathSimulationResult Run(
        CraftState craft,
        StepState root,
        ISolverDefinition solverDefinition,
        VulcanSolverMode? liveRecoveryMode,
        PluginPathSimulationScenario scenario,
        Func<bool>? cancellationRequested = null)
    {
        var trace = new List<PluginPathSimulationTraceEntry>();
        var actionRng = new XorShift32(NonZeroSeed(scenario.GameSeed));
        var conditionRng = new XorShift32(NonZeroSeed(Mix(scenario.GameSeed, 1)));
        var current = root with { };
        var gabrielPolicySeed = (solverDefinition as GabrielSolverDefinition)?.PolicySeed;
        using var processor = new CraftingProcessorSession(
            emitEvents: false,
            emitDiagnostics: false,
            gabrielPolicySeed);
        processor.Setup();
        processor.RegisterSolver(solverDefinition);
        processor.OnCraftStarted(
            craft,
            current,
            craft.RecipeId,
            isTrial: scenario.IsTrial,
            requiredSolverDefinitionType: solverDefinition.GetType());
        if (processor.ActiveSolver == null)
            return Failed(current, trace, processor.FaultReason);

        for (var actionNumber = 1; actionNumber <= MaximumActions; ++actionNumber)
        {
            ThrowIfCancelled(cancellationRequested);
            var recommendation = AwaitRecommendation(processor, cancellationRequested);
            if (recommendation.IsTerminalFailure || recommendation.Action == VulcanSkill.None)
            {
                return new(
                    current,
                    trace,
                    SynthesisCompleted: false,
                    FullQuality: false,
                    SolverTerminalFailure: true,
                    string.IsNullOrWhiteSpace(recommendation.Comment)
                        ? "Plugin solver stopped without a usable action."
                        : recommendation.Comment);
            }

            var manualAction = VulcanSkill.None;
            var manual = scenario.ManualActions?.TryGetValue(actionNumber, out manualAction) == true;
            var executed = manual ? manualAction : recommendation.Action;
            var externalAction = manual && executed != recommendation.Action;
            if (!Simulator.CanUseAction(craft, current, executed))
                return Failed(current, trace, $"Plugin-path action {executed} is unusable at {current}.");

            var previous = current;
            var actionRoll = actionRng.NextUnit();
            var advancesCondition = executed == VulcanSkill.CarefulObservation || !Simulator.SkipUpdates(executed);
            var conditionRoll = advancesCondition ? conditionRng.NextUnit() : 0f;
            var (executeResult, actual) = Simulator.Execute(
                craft,
                previous,
                executed,
                actionRoll,
                conditionRoll);
            if (executeResult == Simulator.ExecuteResult.CantUse)
                return Failed(previous, trace, $"Game simulator rejected plugin-path action {executed}.");
            if (!externalAction && processor.ActiveSolver is DonatelloSolver issued)
                issued.NotifyOpportunisticActionIssued();

            var rolledCondition = actual.Condition;
            if (scenario.ForcedConditions?.TryGetValue(actionNumber, out var forcedCondition) == true)
            {
                if (!advancesCondition)
                    return Failed(previous, trace, $"Forced condition at action {actionNumber} follows a zero-step action.");
                actual.Condition = forcedCondition;
            }
            trace.Add(new(
                actionNumber,
                recommendation.Action,
                executed,
                manual,
                previous.Condition,
                rolledCondition,
                actual.Condition,
                executeResult,
                actual with { }));

            if (actual.Progress >= craft.CraftProgress || actual.Durability <= 0)
            {
                processor.OnCraftFinished(craft, actual, craft.RecipeId, cancelled: false);
                return Completed(craft, actual, trace);
            }

            var observed = Observe(previous, actual);
            if (externalAction)
            {
                if (!StepStateReconciler.TryReconcileExternalAction(
                        craft,
                        previous,
                        observed,
                        out current,
                        out var externalActionObserved,
                        out var inferredAction)
                    || !externalActionObserved
                    || inferredAction != executed)
                {
                    return Failed(actual, trace, $"Could not reconcile manual action {executed} at ordinal {actionNumber}.");
                }
                if (!ExactEquivalent(current, actual))
                    return Failed(actual, trace, $"Manual-action reconciliation drifted from game state at ordinal {actionNumber}.");
                if (!processor.TryAdoptLiveCraft(craft, current, liveRecoveryMode, out var recoveryFailure))
                    return Failed(current, trace, $"Could not recover after manual action {executed}: {recoveryFailure}");
            }
            else
            {
                if (!StepStateReconciler.TryReconcileAction(
                        craft,
                        previous,
                        executed,
                        observed,
                        out current))
                {
                    return Failed(actual, trace, $"Could not reconcile recommended action {executed} at ordinal {actionNumber}.");
                }
                if (!ExactEquivalent(current, actual))
                    return Failed(actual, trace, $"Recommended-action reconciliation drifted from game state at ordinal {actionNumber}.");
                processor.OnCraftAdvanced(craft, current, craft.RecipeId);
            }
        }

        return Failed(current, trace, $"Plugin path exceeded {MaximumActions} actions.");
    }

    private static Solver.Recommendation AwaitRecommendation(
        CraftingProcessorSession processor,
        Func<bool>? cancellationRequested)
    {
        var started = Stopwatch.StartNew();
        while (started.Elapsed < RecommendationTimeout)
        {
            ThrowIfCancelled(cancellationRequested);
            processor.Update();
            var recommendation = processor.NextRecommendation;
            if (recommendation.Action != VulcanSkill.None || recommendation.IsTerminalFailure)
                return recommendation;
            Thread.Sleep(1);
        }
        throw new TimeoutException($"Plugin solver did not produce a recommendation within {RecommendationTimeout}.");
    }

    private static PluginPathSimulationResult Completed(
        CraftState craft,
        StepState final,
        IReadOnlyList<PluginPathSimulationTraceEntry> trace)
    {
        var synthesisCompleted = final.Progress >= craft.CraftProgress;
        return new(
            final,
            trace,
            synthesisCompleted,
            synthesisCompleted && final.Quality >= craft.CraftQualityMax,
            SolverTerminalFailure: false,
            FailureReason: null);
    }

    private static PluginPathSimulationResult Failed(
        StepState final,
        IReadOnlyList<PluginPathSimulationTraceEntry> trace,
        string reason)
        => new(
            final,
            trace,
            SynthesisCompleted: false,
            FullQuality: false,
            SolverTerminalFailure: false,
            reason);

    private static StepState Observe(StepState previous, StepState actual)
        => previous with
        {
            Index = actual.Index,
            Progress = actual.Progress,
            Quality = actual.Quality,
            Durability = actual.Durability,
            RemainingCP = actual.RemainingCP,
            Condition = actual.Condition,
            IQStacks = actual.IQStacks,
            WasteNotLeft = actual.WasteNotLeft,
            ManipulationLeft = actual.ManipulationLeft,
            GreatStridesLeft = actual.GreatStridesLeft,
            InnovationLeft = actual.InnovationLeft,
            VenerationLeft = actual.VenerationLeft,
            MuscleMemoryLeft = actual.MuscleMemoryLeft,
            FinalAppraisalLeft = actual.FinalAppraisalLeft,
            HeartAndSoulActive = actual.HeartAndSoulActive,
            ExpedienceLeft = actual.ExpedienceLeft,
            TrainedPerfectionActive = actual.TrainedPerfectionActive,
            ComboAction = actual.ComboAction,
            MaterialMiracleCharges = actual.MaterialMiracleCharges,
            StellarSteadyHandCharges = actual.StellarSteadyHandCharges,
            StellarSteadyHandLeft = actual.StellarSteadyHandLeft,
        };

    private static void ThrowIfCancelled(Func<bool>? cancellationRequested)
    {
        if (cancellationRequested?.Invoke() == true)
            throw new OperationCanceledException("Plugin-path simulation was cancelled.");
    }

    private static bool ExactEquivalent(StepState left, StepState right)
        => left.Index == right.Index
            && left.Progress == right.Progress
            && left.Quality == right.Quality
            && left.Durability == right.Durability
            && left.RemainingCP == right.RemainingCP
            && left.Condition == right.Condition
            && left.IQStacks == right.IQStacks
            && left.WasteNotLeft == right.WasteNotLeft
            && left.ManipulationLeft == right.ManipulationLeft
            && left.GreatStridesLeft == right.GreatStridesLeft
            && left.InnovationLeft == right.InnovationLeft
            && left.VenerationLeft == right.VenerationLeft
            && left.MuscleMemoryLeft == right.MuscleMemoryLeft
            && left.FinalAppraisalLeft == right.FinalAppraisalLeft
            && left.CarefulObservationLeft == right.CarefulObservationLeft
            && left.CrafterDelineationsLeft == right.CrafterDelineationsLeft
            && left.HeartAndSoulActive == right.HeartAndSoulActive
            && left.HeartAndSoulAvailable == right.HeartAndSoulAvailable
            && left.PrevActionFailed == right.PrevActionFailed
            && left.ExpedienceLeft == right.ExpedienceLeft
            && left.QuickInnoLeft == right.QuickInnoLeft
            && left.QuickInnoAvailable == right.QuickInnoAvailable
            && left.TrainedPerfectionAvailable == right.TrainedPerfectionAvailable
            && left.TrainedPerfectionActive == right.TrainedPerfectionActive
            && left.ComboAction == right.ComboAction
            && left.PrevComboAction == right.PrevComboAction
            && left.MaterialMiracleCharges == right.MaterialMiracleCharges
            && left.StellarSteadyHandCharges == right.StellarSteadyHandCharges
            && left.StellarSteadyHandLeft == right.StellarSteadyHandLeft
            && left.StellarSteadyHandsUsed == right.StellarSteadyHandsUsed
            && left.ObserveCounter == right.ObserveCounter;

    private static ulong Mix(ulong seed, ulong stream)
    {
        var value = seed + (stream + 1) * 0x9E37_79B9_7F4A_7C15UL;
        value = (value ^ (value >> 30)) * 0xBF58_476D_1CE4_E5B9UL;
        value = (value ^ (value >> 27)) * 0x94D0_49BB_1331_11EBUL;
        return value ^ (value >> 31);
    }

    private static uint NonZeroSeed(ulong seed)
    {
        var value = (uint)(seed ^ (seed >> 32));
        return value == 0 ? 0x9E37_79B9u : value;
    }

    private sealed class XorShift32(uint seed)
    {
        private uint _state = seed;

        public float NextUnit()
        {
            var value = _state;
            value ^= value << 13;
            value ^= value >> 17;
            value ^= value << 5;
            _state = value;
            return (value >> 8) / 16_777_216f;
        }
    }
}
