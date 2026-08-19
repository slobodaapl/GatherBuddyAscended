using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GatherBuddy.Crafting;
using Newtonsoft.Json;

namespace GatherBuddy.Vulcan;

internal sealed class CraftingProcessorSession : IDisposable
{
    private readonly List<ISolverDefinition> _solverDefinitions = new();
    private readonly bool _emitEvents;
    private readonly bool _emitDiagnostics;
    private readonly ulong? _gabrielPolicySeed;
    private Solver? _activeSolver;
    private Solver.Recommendation _nextRecommendation;
    private string _activeSolverName = "";
    private Task<SolveOutcome>? _pendingSolve;
    private CancellationTokenSource? _pendingCancellation;
    private SolveRequest? _pendingRequest;
    private SolveRequest? _queuedRequest;
    private StepState? _recommendationStep;
    private string _faultReason = string.Empty;

    private sealed record SolveRequest(
        Solver Solver,
        CraftState Craft,
        StepState Step,
        string SolverName,
        bool Resume);

    private sealed record SolveOutcome(
        SolveRequest Request,
        Solver.Recommendation Recommendation,
        Exception? Error);

    internal CraftingProcessorSession(
        bool emitEvents,
        bool emitDiagnostics,
        ulong? gabrielPolicySeed = null)
    {
        _emitEvents = emitEvents;
        _emitDiagnostics = emitDiagnostics;
        _gabrielPolicySeed = gabrielPolicySeed;
    }

    public IReadOnlyList<ISolverDefinition> SolverDefinitions => _solverDefinitions.AsReadOnly();
    public Solver.Recommendation NextRecommendation => _nextRecommendation;
    public string ActiveSolverName => _activeSolverName;
    public bool IsActive => _activeSolver != null;
    public string FaultReason => _faultReason;
    internal Solver? ActiveSolver => _activeSolver;

    public void Setup()
    {
        _solverDefinitions.Clear();
        _solverDefinitions.Add(new ProgressOnlySolverDefinition());
    }

    public void Dispose()
    {
        var solver = _activeSolver;
        AbandonPendingWork();
        if (solver is DonatelloSolver donatello
            && !donatello.WaitForPendingSolve(TimeSpan.FromSeconds(5)))
            Error("[CraftingProcessor] Donatello did not stop within five seconds during plugin shutdown");
        _activeSolver = null;
        _nextRecommendation = new(VulcanSkill.None);
        _activeSolverName = "";
        _recommendationStep = null;
        _faultReason = string.Empty;
    }

    public IEnumerable<ISolverDefinition.Desc> GetAvailableSolversForCraft(CraftState craft, bool includeUnsupported = false)
    {
        foreach (var definition in _solverDefinitions)
        {
            foreach (var flavor in definition.Flavors(craft))
            {
                if (includeUnsupported || flavor.UnsupportedReason.Length == 0)
                    yield return flavor;
            }
        }
    }

    public ISolverDefinition.Desc? FindBestSolver(CraftState craft)
    {
        var available = GetAvailableSolversForCraft(craft, false).ToList();
        return available.Count > 0 ? available.MaxBy(x => x.Priority) : null;
    }

    public void OnCraftStarted(
        CraftState craft,
        StepState initialStep,
        uint recipeId,
        bool isTrial,
        Type? requiredSolverDefinitionType = null)
    {
        AbandonPendingWork();
        _faultReason = string.Empty;
        Debug($"[CraftingProcessor] OnCraftStarted: recipe={recipeId}, trial={isTrial}, solvers available={_solverDefinitions.Count}");
        var bestSolver = requiredSolverDefinitionType == null
            ? FindBestSolver(craft)
            : FindRequiredSolver(craft, requiredSolverDefinitionType);
        Debug($"[CraftingProcessor] FindBestSolver result: {(bestSolver == null ? "null" : bestSolver.Value.Name)}");
        if (_emitDiagnostics)
        {
            GatherBuddy.Log.Information(FormatCraftStartLogLine(
                craft,
                initialStep,
                recipeId,
                isTrial,
                bestSolver?.Name ?? "none",
                bestSolver?.UnsupportedReason));
        }
        if (bestSolver == null || bestSolver.Value.UnsupportedReason.Length > 0)
        {
            _faultReason = bestSolver == null
                ? "No crafting solver is available"
                : $"No crafting solver is available: {bestSolver.Value.UnsupportedReason}";
            Error($"[CraftingProcessor] {_faultReason}");
            _activeSolver = null;
            _activeSolverName = "";
            return;
        }

        _activeSolver = bestSolver.Value.CreateSolver(craft);
        _activeSolverName = bestSolver.Value.Name;

        if (_activeSolver == null)
        {
            _faultReason = $"Failed to create {bestSolver.Value.Name}";
            Error($"[CraftingProcessor] {_faultReason}");
            return;
        }

        Submit(craft, initialStep, resume: false);
    }

    internal static string FormatCraftStartLogLine(
        CraftState craft,
        StepState initialStep,
        uint recipeId,
        bool isTrial,
        string solverName,
        string? unsupportedReason = null)
    {
        var options = craft.DonatelloOptions;
        var config = GatherBuddy.Config.RaphaelSolverConfig;
        var specialistActionsAllowed = CraftingContextResolver.ResolveSpecialistActionsAllowed(craft);
        var effectiveSpecialist = craft.Specialist && specialistActionsAllowed;
        var json = JsonConvert.SerializeObject(new
        {
            recipeId,
            itemId = craft.ItemId,
            trial = isTrial,
            solver = solverName,
            unsupportedReason = string.IsNullOrEmpty(unsupportedReason) ? null : unsupportedReason,
            player = new
            {
                level = craft.StatLevel,
                craftsmanship = craft.StatCraftsmanship,
                control = craft.StatControl,
                cpMax = craft.StatCP,
                manipulationUnlocked = craft.UnlockedManipulation,
                specialistActive = craft.Specialist,
                specialistActionsAllowed,
                solverSpecialist = effectiveSpecialist,
                crafterDelineations = craft.CrafterDelineations,
                solverDelineations = RaphaelSolveRequest.CanonicalizeCrafterDelineations(
                    effectiveSpecialist,
                    craft.CrafterDelineations),
                splendorCosmic = craft.SplendorCosmic,
            },
            recipe = new
            {
                recipeLevelTableId = craft.RecipeLevelTableId,
                level = craft.CraftLevel,
                stars = craft.CraftStars,
                hq = craft.CraftHQ,
                collectible = craft.CraftCollectible,
                expert = craft.CraftExpert,
                ishgardExpert = craft.IshgardExpert,
                cosmic = craft.IsCosmic,
                durabilityMax = craft.CraftDurability,
                progressMax = craft.CraftProgress,
                qualityMax = craft.CraftQualityMax,
                progressDivider = craft.CraftProgressDivider,
                progressModifier = craft.CraftProgressModifier,
                qualityDivider = craft.CraftQualityDivider,
                qualityModifier = craft.CraftQualityModifier,
                qualityMin1 = craft.CraftQualityMin1,
                qualityMin2 = craft.CraftQualityMin2,
                qualityMin3 = craft.CraftQualityMin3,
                requiredQuality = craft.CraftRequiredQuality,
                recommendedCraftsmanship = craft.CraftRecommendedCraftsmanship,
                conditionFlags = (ushort)craft.ConditionFlags,
                conditionProfileCataloged = craft.CraftConditionProfileCataloged,
                conditionProbabilities = craft.CraftConditionProbabilities,
            },
            start = new
            {
                configuredInitialQuality = craft.InitialQuality,
                step = initialStep.Index,
                progress = initialStep.Progress,
                quality = initialStep.Quality,
                durability = initialStep.Durability,
                cp = initialStep.RemainingCP,
                condition = initialStep.Condition.ToString(),
                delineations = initialStep.CrafterDelineationsLeft,
                materialMiracleCharges = initialStep.MaterialMiracleCharges,
                stellarSteadyHandCharges = initialStep.StellarSteadyHandCharges,
            },
            mission = new
            {
                hasMaterialMiracle = craft.MissionHasMaterialMiracle,
                materialMiracleCharges = craft.CurrentMaterialMiracleCharges,
                hasStellarSteadyHand = craft.MissionHasStellarSteadyHand,
                stellarSteadyHandCharges = craft.CurrentStellarSteadyHandCharges,
            },
            donatello = new
            {
                objective = (options?.Objective ?? DonatelloSolveObjective.MaximizeQuality).ToString(),
                minimizeSteps = options?.MinimizeSteps ?? config.DonatelloMinimizeSteps,
                maxStellarSteadyHandUses = options?.MaxStellarSteadyHandUses ?? 0,
                maximizeQualityAtCostOfTime = options?.MaximizeQualityAtCostOfTime ?? false,
                specialistActionsOverride = options?.AllowSpecialistActions,
                replanDeadlineOverrideMillis = options?.ReplanDeadlineMillis,
                improvementQuietOverrideMillis = options?.ImprovementQuietPeriodMillis,
                improvementQuietDeadlineMillis = DonatelloSolver.ResolveImprovementQuietPeriodMillis(
                    craft,
                    config.DonatelloImprovementQuietSeconds),
                gabrielWorkerThreads = craft.GabrielWorkerThreads,
            },
        }, Formatting.None);
        return $"[CraftStart] {json}";
    }

    private ISolverDefinition.Desc? FindRequiredSolver(CraftState craft, Type definitionType)
    {
        var definition = _solverDefinitions.FirstOrDefault(candidate => candidate.GetType() == definitionType);
        if (definition == null)
            return null;

        ISolverDefinition.Desc? best = null;
        foreach (var flavor in definition.Flavors(craft))
        {
            if (best == null || flavor.Priority > best.Value.Priority)
                best = flavor;
        }
        return best;
    }

    public bool TryAdoptLiveCraft(
        CraftState craft,
        StepState liveStep,
        bool allowDonatelloLiveRecovery,
        out string failureReason)
        => TryAdoptLiveCraft(
            craft,
            liveStep,
            allowDonatelloLiveRecovery ? VulcanSolverMode.Donatello : null,
            out failureReason);

    public bool TryAdoptLiveCraft(
        CraftState craft,
        StepState liveStep,
        VulcanSolverMode? adaptiveRecoveryMode,
        out string failureReason)
    {
        AbandonPendingWork();
        _faultReason = string.Empty;
        Solver? solver = null;
        var solverName = string.Empty;
        if (adaptiveRecoveryMode == VulcanSolverMode.Donatello)
        {
            if (DonatelloSolverDefinition.TryCreateLiveSolver(craft, out var donatello, out failureReason))
            {
                solver = donatello;
                solverName = "Donatello (Live recovery from observed state)";
            }
            else
            {
                return false;
            }
        }
        else if (adaptiveRecoveryMode == VulcanSolverMode.Gabriel)
        {
            if (GabrielSolverDefinition.TryCreateLiveSolver(
                    craft,
                    _gabrielPolicySeed,
                    out var gabriel,
                    out failureReason))
            {
                solver = gabriel;
                solverName = "Gabriel (Live recovery from observed state)";
            }
            else
            {
                return false;
            }
        }

        if (solver == null)
        {
            var bestSolver = FindBestSolver(craft);
            if (bestSolver == null || bestSolver.Value.UnsupportedReason.Length > 0)
            {
                failureReason = bestSolver?.UnsupportedReason ?? "no solver is available";
                return false;
            }

            solver = bestSolver.Value.CreateSolver(craft);
            solverName = bestSolver.Value.Name;
            if (solver is not (ProgressOnlySolver or StandardSolver))
            {
                failureReason = $"{bestSolver.Value.Name} cannot safely recover an unknown mid-craft action index";
                return false;
            }
        }

        _activeSolver = solver;
        _activeSolverName = solverName;
        _nextRecommendation = new(VulcanSkill.None, "Solver resuming live craft");
        _recommendationStep = null;
        _faultReason = string.Empty;
        Submit(craft, liveStep, resume: solver is DonatelloSolver or GabrielSolver);
        failureReason = string.Empty;
        return true;
    }

    public void OnCraftAdvanced(CraftState craft, StepState step, uint? recipeId)
    {
        if (_activeSolver == null)
            return;

        Submit(craft, step, resume: false);
    }

    public bool TryResumeCraft(CraftState craft, StepState step)
    {
        if (_activeSolver is not (DonatelloSolver or GabrielSolver))
            return false;

        Submit(craft, step, resume: true);
        return true;
    }

    public void OnCraftFinished(CraftState craft, StepState finalStep, uint? recipeId, bool cancelled)
    {
        AbandonPendingWork();
        _activeSolver = null;
        _activeSolverName = "";
        _nextRecommendation = new(VulcanSkill.None);
        _recommendationStep = null;
        _faultReason = string.Empty;
    }

    private void AbandonPendingWork()
    {
        var cancellation = _pendingCancellation;
        var pending = _pendingSolve;
        cancellation?.Cancel();
        if (_activeSolver is IDisposable disposable)
            disposable.Dispose();
        if (cancellation != null)
        {
            if (pending is { IsCompleted: false })
                _ = pending.ContinueWith(_ => cancellation.Dispose(), TaskScheduler.Default);
            else
                cancellation.Dispose();
        }
        _pendingCancellation = null;
        _pendingSolve = null;
        _pendingRequest = null;
        _queuedRequest = null;
    }

    public void Update()
    {
        if (_pendingSolve?.IsCompleted == true)
        {
            var outcome = _pendingSolve.GetAwaiter().GetResult();
            _pendingCancellation?.Dispose();
            _pendingCancellation = null;
            _pendingSolve = null;
            _pendingRequest = null;

            if (_queuedRequest is { } queued)
            {
                _queuedRequest = null;
                Start(queued);
                return;
            }

            if (!ReferenceEquals(_activeSolver, outcome.Request.Solver))
                return;

            if (outcome.Error != null)
            {
                _faultReason = $"{outcome.Request.SolverName} failed: {outcome.Error.Message}";
                _nextRecommendation = new(VulcanSkill.None, _faultReason, IsTerminalFailure: true);
                _recommendationStep = outcome.Request.Step;
                Error($"[CraftingProcessor] Background solver failed: {outcome.Error}");
            }
            else
            {
                _nextRecommendation = outcome.Recommendation;
                _recommendationStep = outcome.Request.Step;
                if (_nextRecommendation.IsTerminalFailure)
                {
                    _faultReason = string.IsNullOrWhiteSpace(_nextRecommendation.Comment)
                        ? $"{outcome.Request.SolverName} stopped without a usable action"
                        : _nextRecommendation.Comment;
                    Error($"[CraftingProcessor] {_faultReason}");
                }
                Debug($"[CraftingProcessor] Background recommendation: {_nextRecommendation.Action}");
            }

            if (_emitEvents)
            {
                CraftingEvents.RaiseSolverRecommendationReady(
                    outcome.Request.Craft,
                    outcome.Request.Step,
                    _nextRecommendation,
                    outcome.Request.SolverName);
            }
        }

        RefreshOpportunisticRecommendation();
    }

    private void RefreshOpportunisticRecommendation()
    {
        if (_activeSolver is not DonatelloSolver donatello
            || _recommendationStep == null
            || _nextRecommendation.Action == VulcanSkill.None)
            return;
        if (!donatello.TryApplyCompletedOpportunisticReplan(_recommendationStep, out var refreshed)
            || refreshed.Action == VulcanSkill.None)
            return;

        _nextRecommendation = refreshed;
        Debug($"[CraftingProcessor] Opportunistic Donatello recommendation: {refreshed.Action}");
    }

    private void Submit(CraftState craft, StepState step, bool resume)
    {
        if (_activeSolver == null)
            return;
        if (_faultReason.Length > 0)
            return;
        if (!resume
            && _nextRecommendation.Action != VulcanSkill.None
            && _recommendationStep != null
            && Equivalent(_recommendationStep, step))
            return;
        if (_pendingRequest is { } pending
            && ReferenceEquals(pending.Solver, _activeSolver)
            && Equivalent(pending.Step, step)
            && (pending.Resume || !resume))
            return;
        if (_queuedRequest is { } queued
            && ReferenceEquals(queued.Solver, _activeSolver)
            && Equivalent(queued.Step, step)
            && (queued.Resume || !resume))
            return;

        var request = new SolveRequest(
            _activeSolver,
            craft with { CraftConditionProbabilities = [.. craft.CraftConditionProbabilities] },
            step with { },
            _activeSolverName,
            resume);
        _nextRecommendation = new(VulcanSkill.None, "Solver calculating");
        _recommendationStep = null;
        if (_pendingSolve == null)
            Start(request);
        else
        {
            _queuedRequest = request;
            _pendingCancellation?.Cancel();
        }
    }

    private void Start(SolveRequest request)
    {
        _pendingRequest = request;
        _pendingCancellation = new CancellationTokenSource();
        var cancellationToken = _pendingCancellation.Token;
        _pendingSolve = Task.Run(async () =>
        {
            try
            {
                var recommendation = request.Solver is DonatelloSolver donatello
                    ? await donatello.SolveUntilReadyAsync(
                        request.Craft,
                        request.Step,
                        request.Resume,
                        cancellationToken).ConfigureAwait(false)
                    : request.Solver.Solve(request.Craft, request.Step);
                return new SolveOutcome(request, recommendation, null);
            }
            catch (Exception exception)
            {
                return new SolveOutcome(request, new(VulcanSkill.None), exception);
            }
        });
    }

    private static bool Equivalent(StepState left, StepState right)
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

    public void RegisterSolver(ISolverDefinition definition)
    {
        if (!_solverDefinitions.Any(s => s.GetType() == definition.GetType()))
            _solverDefinitions.Add(definition);
    }

    public void UnregisterSolver(ISolverDefinition definition)
    {
        _solverDefinitions.RemoveAll(s => s.GetType() == definition.GetType());
    }

    private void Debug(string message)
    {
        if (_emitDiagnostics)
            GatherBuddy.Log.Debug(message);
    }

    private void Error(string message)
    {
        if (_emitDiagnostics)
            GatherBuddy.Log.Error(message);
    }
}

public static class CraftingProcessor
{
    private static readonly CraftingProcessorSession Live = new(emitEvents: true, emitDiagnostics: true);

    public static IReadOnlyList<ISolverDefinition> SolverDefinitions => Live.SolverDefinitions;
    public static Solver.Recommendation NextRecommendation => Live.NextRecommendation;
    public static string ActiveSolverName => Live.ActiveSolverName;
    public static bool IsActive => Live.IsActive;
    public static string FaultReason => Live.FaultReason;
    internal static Solver? ActiveSolver => Live.ActiveSolver;

    public static void Setup() => Live.Setup();
    public static void Dispose() => Live.Dispose();
    public static IEnumerable<ISolverDefinition.Desc> GetAvailableSolversForCraft(
        CraftState craft,
        bool includeUnsupported = false)
        => Live.GetAvailableSolversForCraft(craft, includeUnsupported);
    public static ISolverDefinition.Desc? FindBestSolver(CraftState craft)
        => Live.FindBestSolver(craft);
    public static void OnCraftStarted(
        CraftState craft,
        StepState initialStep,
        uint recipeId,
        bool isTrial,
        Type? requiredSolverDefinitionType = null)
        => Live.OnCraftStarted(craft, initialStep, recipeId, isTrial, requiredSolverDefinitionType);
    public static bool TryAdoptLiveCraft(
        CraftState craft,
        StepState liveStep,
        bool allowDonatelloLiveRecovery,
        out string failureReason)
        => Live.TryAdoptLiveCraft(craft, liveStep, allowDonatelloLiveRecovery, out failureReason);
    public static bool TryAdoptLiveCraft(
        CraftState craft,
        StepState liveStep,
        VulcanSolverMode? adaptiveRecoveryMode,
        out string failureReason)
        => Live.TryAdoptLiveCraft(craft, liveStep, adaptiveRecoveryMode, out failureReason);
    public static void OnCraftAdvanced(CraftState craft, StepState step, uint? recipeId)
        => Live.OnCraftAdvanced(craft, step, recipeId);
    public static bool TryResumeCraft(CraftState craft, StepState step)
        => Live.TryResumeCraft(craft, step);
    public static void OnCraftFinished(
        CraftState craft,
        StepState finalStep,
        uint? recipeId,
        bool cancelled)
        => Live.OnCraftFinished(craft, finalStep, recipeId, cancelled);
    public static void Update() => Live.Update();
    public static void RegisterSolver(ISolverDefinition definition) => Live.RegisterSolver(definition);
    public static void UnregisterSolver(ISolverDefinition definition) => Live.UnregisterSolver(definition);
}
