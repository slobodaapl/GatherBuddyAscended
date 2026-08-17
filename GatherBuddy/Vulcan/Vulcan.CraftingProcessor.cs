using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace GatherBuddy.Vulcan;

public static class CraftingProcessor
{
    private static readonly List<ISolverDefinition> _solverDefinitions = new();
    private static Solver? _activeSolver;
    private static Solver.Recommendation _nextRecommendation;
    private static string _activeSolverName = "";
    private static Task<SolveOutcome>? _pendingSolve;
    private static SolveRequest? _pendingRequest;
    private static SolveRequest? _queuedRequest;
    private static StepState? _recommendationStep;
    private static string _faultReason = string.Empty;

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

    public static IReadOnlyList<ISolverDefinition> SolverDefinitions => _solverDefinitions.AsReadOnly();
    public static Solver.Recommendation NextRecommendation => _nextRecommendation;
    public static string ActiveSolverName => _activeSolverName;
    public static bool IsActive => _activeSolver != null;
    public static string FaultReason => _faultReason;
    internal static Solver? ActiveSolver => _activeSolver;

    public static void Setup()
    {
        _solverDefinitions.Clear();
        _solverDefinitions.Add(new ProgressOnlySolverDefinition());
    }

    public static void Dispose()
    {
        var solver = _activeSolver;
        AbandonPendingWork();
        if (solver is DonatelloSolver donatello
            && !donatello.WaitForPendingSolve(TimeSpan.FromSeconds(5)))
            GatherBuddy.Log.Error("[CraftingProcessor] Donatello did not stop within five seconds during plugin shutdown");
        _activeSolver = null;
        _nextRecommendation = new(VulcanSkill.None);
        _activeSolverName = "";
        _recommendationStep = null;
        _faultReason = string.Empty;
    }

    public static IEnumerable<ISolverDefinition.Desc> GetAvailableSolversForCraft(CraftState craft, bool includeUnsupported = false)
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

    public static ISolverDefinition.Desc? FindBestSolver(CraftState craft)
    {
        var available = GetAvailableSolversForCraft(craft, false).ToList();
        return available.Count > 0 ? available.MaxBy(x => x.Priority) : null;
    }

    public static void OnCraftStarted(CraftState craft, StepState initialStep, uint recipeId, bool isTrial)
    {
        AbandonPendingWork();
        _faultReason = string.Empty;
        GatherBuddy.Log.Debug($"[CraftingProcessor] OnCraftStarted: recipe={recipeId}, solvers available={_solverDefinitions.Count}");
        var bestSolver = FindBestSolver(craft);
        GatherBuddy.Log.Debug($"[CraftingProcessor] FindBestSolver result: {(bestSolver == null ? "null" : bestSolver.Value.Name)}");
        if (bestSolver == null || bestSolver.Value.UnsupportedReason.Length > 0)
        {
            _faultReason = bestSolver == null
                ? "No crafting solver is available"
                : $"No crafting solver is available: {bestSolver.Value.UnsupportedReason}";
            GatherBuddy.Log.Error($"[CraftingProcessor] {_faultReason}");
            _activeSolver = null;
            _activeSolverName = "";
            return;
        }

        _activeSolver = bestSolver.Value.CreateSolver(craft);
        _activeSolverName = bestSolver.Value.Name;

        if (_activeSolver == null)
        {
            _faultReason = $"Failed to create {bestSolver.Value.Name}";
            GatherBuddy.Log.Error($"[CraftingProcessor] {_faultReason}");
            return;
        }

        Submit(craft, initialStep, resume: false);
    }

    public static bool TryAdoptLiveCraft(
        CraftState craft,
        StepState liveStep,
        bool allowDonatelloLiveRecovery,
        out string failureReason)
    {
        AbandonPendingWork();
        _faultReason = string.Empty;
        Solver? solver = null;
        var solverName = string.Empty;
        if (allowDonatelloLiveRecovery)
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
        Submit(craft, liveStep, resume: solver is DonatelloSolver);
        failureReason = string.Empty;
        return true;
    }

    public static void OnCraftAdvanced(CraftState craft, StepState step, uint? recipeId)
    {
        if (_activeSolver == null)
            return;

        Submit(craft, step, resume: false);
    }

    public static bool TryResumeCraft(CraftState craft, StepState step)
    {
        if (_activeSolver is not DonatelloSolver donatello)
            return false;

        Submit(craft, step, resume: true);
        return true;
    }

    public static void OnCraftFinished(CraftState craft, StepState finalStep, uint? recipeId, bool cancelled)
    {
        AbandonPendingWork();
        _activeSolver = null;
        _activeSolverName = "";
        _nextRecommendation = new(VulcanSkill.None);
        _recommendationStep = null;
        _faultReason = string.Empty;
    }

    private static void AbandonPendingWork()
    {
        if (_activeSolver is IDisposable disposable)
            disposable.Dispose();
        _pendingSolve = null;
        _pendingRequest = null;
        _queuedRequest = null;
    }

    public static void Update()
    {
        if (_pendingSolve?.IsCompleted != true)
            return;

        var outcome = _pendingSolve.GetAwaiter().GetResult();
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
            GatherBuddy.Log.Error($"[CraftingProcessor] Background solver failed: {outcome.Error}");
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
                GatherBuddy.Log.Error($"[CraftingProcessor] {_faultReason}");
            }
            GatherBuddy.Log.Debug($"[CraftingProcessor] Background recommendation: {_nextRecommendation.Action}");
        }

        CraftingEvents.RaiseSolverRecommendationReady(
            outcome.Request.Craft,
            outcome.Request.Step,
            _nextRecommendation,
            outcome.Request.SolverName);
    }

    private static void Submit(CraftState craft, StepState step, bool resume)
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
            _queuedRequest = request;
    }

    private static void Start(SolveRequest request)
    {
        _pendingRequest = request;
        _pendingSolve = Task.Run(async () =>
        {
            try
            {
                var recommendation = request.Solver is DonatelloSolver donatello
                    ? await donatello.SolveUntilReadyAsync(request.Craft, request.Step, request.Resume).ConfigureAwait(false)
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
            && left.StellarSteadyHandCharges == right.StellarSteadyHandCharges
            && left.StellarSteadyHandLeft == right.StellarSteadyHandLeft
            && left.StellarSteadyHandsUsed == right.StellarSteadyHandsUsed
            && left.ObserveCounter == right.ObserveCounter;

    public static void RegisterSolver(ISolverDefinition definition)
    {
        if (!_solverDefinitions.Any(s => s.GetType() == definition.GetType()))
            _solverDefinitions.Add(definition);
    }

    public static void UnregisterSolver(ISolverDefinition definition)
    {
        _solverDefinitions.RemoveAll(s => s.GetType() == definition.GetType());
    }
}
