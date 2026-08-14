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

    public static void Setup()
    {
        _solverDefinitions.Clear();
        _solverDefinitions.Add(new ProgressOnlySolverDefinition());
    }

    public static void Dispose()
    {
        _activeSolver = null;
        _nextRecommendation = new(VulcanSkill.None);
        _activeSolverName = "";
        _queuedRequest = null;
        _recommendationStep = null;
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
        GatherBuddy.Log.Debug($"[CraftingProcessor] OnCraftStarted: recipe={recipeId}, solvers available={_solverDefinitions.Count}");
        var bestSolver = FindBestSolver(craft);
        GatherBuddy.Log.Debug($"[CraftingProcessor] FindBestSolver result: {(bestSolver == null ? "null" : bestSolver.Value.Name)}");
        if (bestSolver == null || bestSolver.Value.UnsupportedReason.Length > 0)
        {
            GatherBuddy.Log.Warning($"[CraftingProcessor] No solver available. Reason: {(bestSolver == null ? "null" : bestSolver.Value.UnsupportedReason)}");
            _activeSolver = null;
            _activeSolverName = "";
            return;
        }

        _activeSolver = bestSolver.Value.CreateSolver(craft);
        _activeSolverName = bestSolver.Value.Name;

        if (_activeSolver == null)
        {
            GatherBuddy.Log.Error($"[CraftingProcessor] Failed to create solver instance");
            return;
        }

        Submit(craft, initialStep, resume: false);
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
        _activeSolver = null;
        _activeSolverName = "";
        _nextRecommendation = new(VulcanSkill.None);
        _queuedRequest = null;
        _recommendationStep = null;
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
            _nextRecommendation = new(VulcanSkill.None, "Solver failed");
            _recommendationStep = outcome.Request.Step;
            GatherBuddy.Log.Error($"[CraftingProcessor] Background solver failed: {outcome.Error}");
        }
        else
        {
            _nextRecommendation = outcome.Recommendation;
            _recommendationStep = outcome.Request.Step;
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
        if (!resume
            && _nextRecommendation.Action != VulcanSkill.None
            && _recommendationStep != null
            && Equivalent(_recommendationStep, step))
            return;
        if (_pendingRequest is { } pending
            && ReferenceEquals(pending.Solver, _activeSolver)
            && pending.Resume == resume
            && Equivalent(pending.Step, step))
            return;
        if (_queuedRequest is { } queued
            && ReferenceEquals(queued.Solver, _activeSolver)
            && queued.Resume == resume
            && Equivalent(queued.Step, step))
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
        _pendingSolve = Task.Run(() =>
        {
            try
            {
                var recommendation = request.Resume
                    ? ((DonatelloSolver)request.Solver).ResumeFromLiveState(request.Step)
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
            && left.PrevComboAction == right.PrevComboAction
            && left.MaterialMiracleCharges == right.MaterialMiracleCharges
            && left.MaterialMiracleActive == right.MaterialMiracleActive
            && left.MaterialMiraclesUsed == right.MaterialMiraclesUsed
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
