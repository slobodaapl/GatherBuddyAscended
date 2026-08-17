using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GatherBuddy.Crafting;

namespace GatherBuddy.Vulcan;

public sealed class DonatelloSolverDefinition : ISolverDefinition
{
    private readonly RaphaelSolveCoordinator _coordinator;

    public DonatelloSolverDefinition(RaphaelSolveCoordinator coordinator)
        => _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    public IEnumerable<ISolverDefinition.Desc> Flavors(CraftState craft)
    {
        var unsupported = UnsupportedReason(craft);
        if (unsupported.Length > 0)
        {
            yield return new(this, 0, 100, "Donatello", unsupported);
            yield break;
        }
        var request = RaphaelSolveRequest.FromCraftState(
            craft,
            CraftingContextResolver.ResolveSpecialistActionsAllowed(craft));
        if (_coordinator.TryGetSolution(request, out _))
            yield return new(this, 0, 100, "Donatello (Ready)");
        else if (_coordinator.IsSolveInProgress(request))
            yield return new(this, 0, 50, "Donatello (Generating...)", "Initial Raphael solution is still being generated");
        else
            yield return new(this, 0, 25, "Donatello (Not Ready)", "Initial Raphael solution is unavailable");
    }

    public Solver Create(CraftState craft, int flavor)
    {
        var request = RaphaelSolveRequest.FromCraftState(
            craft,
            CraftingContextResolver.ResolveSpecialistActionsAllowed(craft));
        if (!_coordinator.TryGetSolution(request, out var solution) || solution == null)
            return null!;
        return CreateFromSolution(solution, craft);
    }

    internal static Solver CreateFromSolution(CachedRaphaelSolution solution, CraftState craft)
    {
        var actions = solution.ActionIds.ConvertAll(id => (VulcanSkill)id);
        var initial = GameStateBuilder.BuildInitialStepState(craft, craft.InitialQuality);
        var evaluation = DonatelloPlanEvaluator.Evaluate(craft, initial, actions);
        if (ShouldUseStaticPlan(craft, evaluation))
        {
            GatherBuddy.Log.Debug("[Donatello] Initial Raphael plan already reaches maximum quality; using static plan");
            return new RaphaelMacroSolver(solution, craft);
        }
        return new DonatelloSolver(solution, craft);
    }

    internal static bool TryCreateLiveSolver(
        CraftState craft,
        out DonatelloSolver? solver,
        out string failureReason)
    {
        failureReason = UnsupportedReason(craft);
        if (failureReason.Length > 0)
        {
            solver = null;
            return false;
        }

        solver = new DonatelloSolver(craft);
        return true;
    }

    internal static bool ShouldUseStaticPlan(CraftState craft, DonatelloPlanEvaluation evaluation)
        => evaluation.Completes && evaluation.Quality >= craft.CraftQualityMax;

    internal static bool IsGuaranteedMaximumQualitySolution(
        CachedRaphaelSolution? solution,
        CraftState craft)
    {
        if (solution == null || solution.IsFailed || solution.ActionIds.Count == 0)
            return false;
        var actions = solution.ActionIds.ConvertAll(id => (VulcanSkill)id);
        var initial = GameStateBuilder.BuildInitialStepState(craft, craft.InitialQuality);
        return ShouldUseStaticPlan(craft, DonatelloPlanEvaluator.Evaluate(craft, initial, actions));
    }

    private static string UnsupportedReason(CraftState craft)
    {
        var supported = ConditionFlags.Normal | ConditionFlags.Good | ConditionFlags.Excellent
            | ConditionFlags.Poor | ConditionFlags.Centered | ConditionFlags.Sturdy
            | ConditionFlags.Pliant | ConditionFlags.Malleable | ConditionFlags.Primed
            | ConditionFlags.GoodOmen | ConditionFlags.Robust;
        return (craft.ConditionFlags & ~supported) != 0
            ? "Recipe exposes unsupported crafting conditions"
            : string.Empty;
    }
}

public sealed class DonatelloSolver : Solver, IDisposable
{
    internal const int DefaultLiveReplanDeadlineMillis = 2000;
    private readonly CraftState _craft;
    private List<VulcanSkill> _plan;
    private int _actionIndex;
    private StepState? _expectedState;
    private Task<DonatelloNative.SolveResult>? _pendingSolve;
    private StepState? _pendingRoot;
    private List<VulcanSkill>? _pendingIncumbent;
    private StepState? _resumeRootAfterPending;
    private string? _handledRoot;
    private IntPtr _pendingInterrupt;
    private DateTime _pendingStartedAt;
    private bool _interruptRequested;
    private bool _progressFallback;
    private string? _protectedQualityFailure;
    private bool _pendingEstablishesBaseline;
    private int? _progressBoundaryActionCount;
    private bool _needsInitialStagedReplan;
    private bool _replanAtProgressBoundary;
    internal int NativeReplanCount { get; private set; }
    private readonly object _interruptLock = new();
    private bool _disposed;
    private readonly ProgressOnlySolver _progressOnlySolver = new();

    public DonatelloSolver(CachedRaphaelSolution initialSolution, CraftState craft)
    {
        _craft = craft ?? throw new ArgumentNullException(nameof(craft));
        _plan = initialSolution?.ActionIds.ConvertAll(id => (VulcanSkill)id)
            ?? throw new ArgumentNullException(nameof(initialSolution));
        _needsInitialStagedReplan = !craft.CraftExpert && !IsProgressOnly(craft);
    }

    internal DonatelloSolver(CraftState craft)
    {
        _craft = craft ?? throw new ArgumentNullException(nameof(craft));
        _plan = [];
    }

    public override Recommendation Solve(CraftState craft, StepState step)
    {
        if (_disposed)
            return new(VulcanSkill.None, "Donatello solver was cancelled", IsTerminalFailure: true);
        if (_protectedQualityFailure != null)
            return new(VulcanSkill.None, _protectedQualityFailure, IsTerminalFailure: true);
        if (_progressFallback)
            return _progressOnlySolver.Solve(craft, step) with { Comment = "Donatello completion fallback" };

        if (_pendingSolve != null)
        {
            if (!_pendingSolve.IsCompleted)
            {
                var timeout = TimeSpan.FromMinutes(
                    GatherBuddy.Config.RaphaelSolverConfig.RaphaelTimeoutMinutes);
                if (!_interruptRequested && DateTime.UtcNow - _pendingStartedAt >= timeout)
                {
                    _interruptRequested = true;
                    RequestInterrupt();
                    GatherBuddy.Log.Warning("[Donatello] Replan timed out; interrupting native search and retaining incumbent");
                }
                return new(VulcanSkill.None, "Donatello re-optimizing remaining craft");
            }
            CompleteReplan();
            if (_protectedQualityFailure != null)
                return new(VulcanSkill.None, _protectedQualityFailure, IsTerminalFailure: true);
            if (_resumeRootAfterPending != null)
            {
                var resumeRoot = _resumeRootAfterPending;
                _resumeRootAfterPending = null;
                return StartResumeReplan(resumeRoot);
            }
        }

        if (_actionIndex < _plan.Count && ShouldUseCarefulObservation(_craft, step))
        {
            var (observationResult, observationExpected) = Simulator.Execute(_craft, step, VulcanSkill.CarefulObservation, 0, 1);
            if (observationResult != Simulator.ExecuteResult.CantUse)
            {
                _expectedState = observationExpected;
                return new(VulcanSkill.CarefulObservation, "Donatello rerolling Normal condition with surplus delineation");
            }
        }

        var rootKey = Fingerprint(step);
        if (_needsInitialStagedReplan || _replanAtProgressBoundary)
        {
            _needsInitialStagedReplan = false;
            _replanAtProgressBoundary = false;
            StartReplan(step, rootKey);
            return new(VulcanSkill.None, "Donatello preparing staged progress plan");
        }

        var progressConditionReaction = !IsProgressOnly(_craft)
            && !_craft.CraftExpert
            && step.Progress < _craft.CraftProgress - 1
            && step.Quality < _craft.CraftQualityMax
            && step.Condition is Condition.Good or Condition.Excellent;
        var poorCarefulObservationReaction = ShouldPlanCarefulObservation(_craft, step);
        if ((RequiresReplan(_craft, _expectedState, step)
                || progressConditionReaction
                || poorCarefulObservationReaction)
            && rootKey != _handledRoot)
        {
            if (CanRepresentLiveRoot(step, out var unsupportedReason))
            {
                StartReplan(step, rootKey);
                return new(VulcanSkill.None, "Donatello re-optimizing remaining craft");
            }
            _handledRoot = rootKey;
            GatherBuddy.Log.Warning($"[Donatello] Replan skipped; retaining incumbent: {unsupportedReason}");
        }

        if (_actionIndex >= _plan.Count)
            return new(VulcanSkill.None, "Donatello plan exhausted before craft completion", IsTerminalFailure: true);

        var action = _plan[_actionIndex];
        if (rootKey != _handledRoot
            && ShouldReplanBeforeCompletion(_craft, step, action))
        {
            if (CanRepresentLiveRoot(step, out var unsupportedReason))
            {
                StartReplan(step, rootKey);
                return new(VulcanSkill.None, "Donatello re-optimizing before below-maximum-quality completion");
            }
            _handledRoot = rootKey;
            GatherBuddy.Log.Warning($"[Donatello] Quality-preserving replan skipped; retaining incumbent: {unsupportedReason}");
        }
        var (result, expected) = Simulator.Execute(_craft, step, action, 0, 1);
        if (result == Simulator.ExecuteResult.CantUse)
        {
            ActivateCompletionFallback($"refused unusable planned action {action}");
            return FailureOrCompletionFallback(craft, step);
        }
        _actionIndex++;
        _expectedState = expected;
        if (_progressBoundaryActionCount is int progressBoundary
            && _actionIndex == progressBoundary)
            _replanAtProgressBoundary = true;
        var phase = _progressBoundaryActionCount is int boundary && _actionIndex <= boundary
            ? "progress"
            : "quality";
        return new(action, $"Donatello {phase} step {_actionIndex}/{_plan.Count}");
    }

    internal async Task<Recommendation> SolveUntilReadyAsync(CraftState craft, StepState step, bool resume)
    {
        while (true)
        {
            var recommendation = resume ? ResumeFromLiveState(step) : Solve(craft, step);
            resume = false;
            var pending = _pendingSolve;
            if (recommendation.Action != VulcanSkill.None || pending == null)
                return recommendation;

            try
            {
                await pending.ConfigureAwait(false);
            }
            catch
            {
                // Solve() consumes the completed task and applies the validated-incumbent fallback.
            }
        }
    }

    internal Recommendation ResumeFromLiveState(StepState step)
    {
        if (_pendingSolve != null)
        {
            if (_pendingSolve.IsCompleted)
            {
                CompleteReplan();
                return StartResumeReplan(step);
            }
            _resumeRootAfterPending = step with { };
            if (!_interruptRequested)
            {
                _interruptRequested = true;
                RequestInterrupt();
            }
            return new(VulcanSkill.None, "Donatello re-optimizing remaining craft");
        }

        return StartResumeReplan(step);
    }

    private Recommendation StartResumeReplan(StepState step)
    {
        var rootKey = Fingerprint(step);
        if (!CanRepresentLiveRoot(step, out var unsupportedReason))
        {
            ActivateCompletionFallback($"cannot resume from the live state: {unsupportedReason}");
            return FailureOrCompletionFallback(_craft, step);
        }

        _progressFallback = false;
        StartReplan(step, rootKey);
        return new(VulcanSkill.None, "Donatello re-optimizing resumed craft from live state");
    }

    private void StartReplan(StepState root, string rootKey)
    {
        NativeReplanCount++;
        var liveRoot = root with { };
        var incumbent = _plan.Skip(_actionIndex).ToList();
        _pendingRoot = liveRoot;
        _pendingIncumbent = incumbent;
        _handledRoot = rootKey;
        _pendingStartedAt = DateTime.UtcNow;
        _interruptRequested = false;
        IntPtr interrupt;
        lock (_interruptLock)
        {
            if (_disposed)
                return;
            _pendingInterrupt = DonatelloNative.CreateInterrupt();
            interrupt = _pendingInterrupt;
        }
        var solveMode = ResolvePendingSolveMode(_craft, incumbent.Count);
        _pendingEstablishesBaseline = solveMode == DonatelloNative.SolveMode.OptimizeQuality
            && ProtectsRaphaelBaseline(_craft)
            && incumbent.Count == 0;
        var raphaelConfig = GatherBuddy.Config.RaphaelSolverConfig;
        var softDeadlineMillis = _pendingEstablishesBaseline
            ? Math.Clamp(raphaelConfig.RaphaelInitialOptimizationSeconds, 1, 300) * 1000
            : ResolveLiveReplanDeadlineMillis(
                _craft,
                raphaelConfig.DonatelloOptimizationThresholdMs,
                GatherBuddy.Config.VulcanExecutionDelayMs);
        var hardDeadlineMillis = _pendingEstablishesBaseline
            ? Math.Clamp(raphaelConfig.RaphaelTimeoutMinutes, 1, 60) * 60 * 1000
            : softDeadlineMillis;
        if (_pendingEstablishesBaseline)
        {
            GatherBuddy.Log.Information(
                $"[Donatello] Establishing fresh live Raphael baseline: root={liveRoot}, "
                + $"targets={_craft.CraftProgress}/{_craft.CraftQualityMax}/{_craft.CraftDurability}, "
                + $"base={Simulator.BaseProgress(_craft)}/{Simulator.BaseQuality(_craft)}, "
                + $"expert={_craft.CraftExpert}, splendorCosmic={_craft.SplendorCosmic}, "
                + $"deadlines={softDeadlineMillis}/{hardDeadlineMillis}ms");
        }
        _pendingSolve = Task.Run(() =>
        {
            try
            {
                return DonatelloNative.SolveDetailed(
                    _craft,
                    liveRoot,
                    CraftingContextResolver.ResolveSpecialistActionsAllowed(_craft),
                    solveMode,
                    interrupt,
                    incumbent,
                    softDeadlineMillis: softDeadlineMillis,
                    hardDeadlineMillis: hardDeadlineMillis);
            }
            finally
            {
                lock (_interruptLock)
                {
                    if (_pendingInterrupt == interrupt)
                        _pendingInterrupt = IntPtr.Zero;
                    DonatelloNative.FreeInterrupt(interrupt);
                }
            }
        });
    }

    internal static int ResolveLiveReplanDeadlineMillis(
        CraftState craft,
        int configuredDeadlineMillis,
        int actionDelayMillis)
        => craft.DonatelloOptions?.MaximizeQualityAtCostOfTime == true
            ? 30_000
            : Math.Max(
                Math.Clamp(configuredDeadlineMillis, 10, 10_000),
                Math.Clamp(actionDelayMillis, 0, 10_000));

    internal static bool ShouldUseCarefulObservation(CraftState craft, StepState step)
    {
        var options = craft.DonatelloOptions;
        if (options?.MaximizeQualityAtCostOfTime != true
            || options.Objective != DonatelloSolveObjective.MaximizeQuality
            || !craft.Specialist
            || !CraftingContextResolver.ResolveSpecialistActionsAllowed(craft)
            || step.Condition != Condition.Normal
            || step.CarefulObservationLeft <= 0)
            return false;

        var reservedDelineations = (step.HeartAndSoulAvailable ? 1 : 0)
            + (step.QuickInnoLeft > 0 ? 1 : 0);
        return step.CrafterDelineationsLeft > reservedDelineations
            && PreservedBuffCount(step) >= 2
            && Simulator.CanUseAction(craft, step, VulcanSkill.CarefulObservation);
    }

    internal static bool ShouldPlanCarefulObservation(CraftState craft, StepState step)
        => craft.DonatelloOptions is
            {
                MaximizeQualityAtCostOfTime: true,
                Objective: DonatelloSolveObjective.MaximizeQuality,
            }
            && craft.Specialist
            && CraftingContextResolver.ResolveSpecialistActionsAllowed(craft)
            && step.Condition == Condition.Poor
            && Simulator.CanUseAction(craft, step, VulcanSkill.CarefulObservation);

    private static int PreservedBuffCount(StepState step)
        => (step.IQStacks > 0 ? 1 : 0)
            + (step.WasteNotLeft > 0 ? 1 : 0)
            + (step.ManipulationLeft > 0 ? 1 : 0)
            + (step.GreatStridesLeft > 0 ? 1 : 0)
            + (step.InnovationLeft > 0 ? 1 : 0)
            + (step.VenerationLeft > 0 ? 1 : 0)
            + (step.MuscleMemoryLeft > 0 ? 1 : 0)
            + (step.FinalAppraisalLeft > 0 ? 1 : 0)
            + (step.HeartAndSoulActive ? 1 : 0)
            + (step.ExpedienceLeft > 0 ? 1 : 0)
            + (step.TrainedPerfectionActive ? 1 : 0)
            + (step.StellarSteadyHandLeft > 0 ? 1 : 0);

    private void CompleteReplan()
    {
        var root = _pendingRoot!;
        var incumbent = _pendingIncumbent!;
        try
        {
            var result = _pendingSolve!.GetAwaiter().GetResult();
            var candidate = result.Actions.ToList();
            var incumbentScore = DonatelloPlanEvaluator.Evaluate(_craft, root, incumbent);
            var candidateScore = DonatelloPlanEvaluator.Evaluate(_craft, root, candidate);
            var stagedProgressPlan = !IsProgressOnly(_craft)
                && IsValidOneShortBoundary(_craft, result.ProgressBoundary, candidateScore);
            if (_pendingEstablishesBaseline)
            {
                GatherBuddy.Log.Information(
                    $"[Donatello] Fresh live Raphael baseline result: actions=[{string.Join(",", candidate)}], "
                    + $"completes={candidateScore.Completes}, quality={candidateScore.Quality}, "
                    + $"steps={candidateScore.Steps}, optimal={result.Optimal}, bound={result.QualityUpperBound}, "
                    + $"elapsed={result.ElapsedMillis}ms");
            }
            if (ShouldAdoptCandidate(_craft, candidateScore, incumbentScore, stagedProgressPlan))
            {
                _plan = candidate;
                _progressBoundaryActionCount = result.ProgressBoundary?.ActionCount;
                GatherBuddy.Log.Debug(
                    $"[Donatello] Adopted {(_pendingEstablishesBaseline ? "live Raphael baseline" : stagedProgressPlan ? "staged progress plan" : "strict improvement")}: quality={candidateScore.Quality}, steps={candidateScore.Steps}, "
                    + $"optimal={result.Optimal}, bound={result.QualityUpperBound}, elapsed={result.ElapsedMillis}ms");
            }
            else if (incumbentScore.Completes)
            {
                _plan = incumbent;
                _progressBoundaryActionCount = null;
                GatherBuddy.Log.Debug("[Donatello] Retained incumbent; candidate did not prove a strict improvement");
            }
            else
            {
                ActivateCompletionFallback("neither native candidate nor incumbent completes from the live state");
            }
        }
        catch (Exception ex)
        {
            if (DonatelloPlanEvaluator.Evaluate(_craft, root, incumbent).Completes)
            {
                _plan = incumbent;
                GatherBuddy.Log.Warning($"[Donatello] Replan failed; retaining validated incumbent: {ex.Message}");
            }
            else
            {
                ActivateCompletionFallback($"native replan failed and incumbent does not complete: {ex.Message}");
            }
        }
        _actionIndex = 0;
        _expectedState = null;
        _pendingSolve = null;
        _pendingRoot = null;
            _pendingIncumbent = null;
            _pendingEstablishesBaseline = false;
        _pendingInterrupt = IntPtr.Zero;
        _interruptRequested = false;
    }

    private void RequestInterrupt()
    {
        lock (_interruptLock)
            DonatelloNative.Interrupt(_pendingInterrupt);
    }

    public void Dispose()
    {
        lock (_interruptLock)
        {
            if (_disposed)
                return;
            _disposed = true;
            DonatelloNative.Interrupt(_pendingInterrupt);
        }
    }

    internal bool WaitForPendingSolve(TimeSpan timeout)
    {
        var pending = _pendingSolve;
        if (pending == null)
            return true;
        try
        {
            return pending.Wait(timeout);
        }
        catch (AggregateException)
        {
            return pending.IsCompleted;
        }
    }

    internal static bool IsValidOneShortBoundary(
        CraftState craft,
        DonatelloNative.ProgressBoundary? boundary,
        DonatelloPlanEvaluation evaluation)
        => !craft.CraftExpert
            && boundary is { Target: "oneShort", ActionCount: > 0 }
            && boundary.ActionCount < evaluation.Trajectory.Count
            && evaluation.Trajectory[boundary.ActionCount].Progress == craft.CraftProgress - 1;

    private void ActivateCompletionFallback(string reason)
    {
        _plan = [];
        _progressBoundaryActionCount = null;
        if (ProtectsRaphaelBaseline(_craft))
        {
            _protectedQualityFailure = $"Donatello stopped to protect the Raphael quality baseline: {reason}";
            _progressFallback = false;
            GatherBuddy.Log.Error($"[Donatello] {_protectedQualityFailure}");
            return;
        }
        _progressFallback = true;
        GatherBuddy.Log.Error($"[Donatello] {reason}; switching to completion fallback");
    }

    private Recommendation FailureOrCompletionFallback(CraftState craft, StepState step)
        => _protectedQualityFailure != null
            ? new(VulcanSkill.None, _protectedQualityFailure, IsTerminalFailure: true)
            : _progressOnlySolver.Solve(craft, step) with { Comment = "Donatello completion fallback" };

    private static bool CanRepresentLiveRoot(StepState step, out string reason)
    {
        if (step.Condition < Condition.Normal || step.Condition > Condition.Robust)
        {
            reason = "unknown crafting condition";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    private static bool Equivalent(StepState expected, StepState actual, bool ignoreQualityAndCondition)
        => expected.Index == actual.Index
            && expected.Progress == actual.Progress
            && (ignoreQualityAndCondition || Math.Abs(expected.Quality - actual.Quality) <= 1)
            && expected.Durability == actual.Durability
            && expected.RemainingCP == actual.RemainingCP
            && (ignoreQualityAndCondition || expected.Condition == actual.Condition)
            && expected.IQStacks == actual.IQStacks
            && expected.WasteNotLeft == actual.WasteNotLeft
            && expected.ManipulationLeft == actual.ManipulationLeft
            && expected.GreatStridesLeft == actual.GreatStridesLeft
            && expected.InnovationLeft == actual.InnovationLeft
            && expected.VenerationLeft == actual.VenerationLeft
            && expected.MuscleMemoryLeft == actual.MuscleMemoryLeft
            && expected.FinalAppraisalLeft == actual.FinalAppraisalLeft
            && expected.CarefulObservationLeft == actual.CarefulObservationLeft
            && expected.CrafterDelineationsLeft == actual.CrafterDelineationsLeft
            && expected.HeartAndSoulActive == actual.HeartAndSoulActive
            && expected.HeartAndSoulAvailable == actual.HeartAndSoulAvailable
            && expected.ExpedienceLeft == actual.ExpedienceLeft
            && expected.QuickInnoLeft == actual.QuickInnoLeft
            && expected.QuickInnoAvailable == actual.QuickInnoAvailable
            && expected.TrainedPerfectionAvailable == actual.TrainedPerfectionAvailable
            && expected.TrainedPerfectionActive == actual.TrainedPerfectionActive
            && expected.ComboAction == actual.ComboAction
            && expected.PrevComboAction == actual.PrevComboAction
            && expected.StellarSteadyHandCharges == actual.StellarSteadyHandCharges
            && expected.StellarSteadyHandLeft == actual.StellarSteadyHandLeft
            && expected.StellarSteadyHandsUsed == actual.StellarSteadyHandsUsed;

    internal static bool RequiresReplan(CraftState craft, StepState? expected, StepState actual)
    {
        if (IsProgressOnly(craft))
        {
            if (expected == null)
                return IsProgressRelevantCondition(actual.Condition);
            return !Equivalent(expected, actual, ignoreQualityAndCondition: true)
                || expected.Condition != actual.Condition
                    && (IsProgressRelevantCondition(expected.Condition)
                        || IsProgressRelevantCondition(actual.Condition));
        }

        var fullQualityNormalCraft = !craft.CraftExpert && actual.Quality >= craft.CraftQualityMax;
        return expected == null
            ? !fullQualityNormalCraft && actual.Condition != Condition.Normal
            : !Equivalent(expected, actual, fullQualityNormalCraft);
    }

    internal static bool ShouldReplanBeforeCompletion(CraftState craft, StepState step, VulcanSkill action)
    {
        if (IsProgressOnly(craft)
            || craft.CraftExpert
            || craft.IsCosmic
            || step.Quality >= craft.CraftQualityMax
            || Simulator.GetSuccessRate(step, action) < 1.0)
            return false;

        var (result, next) = Simulator.Execute(craft, step, action, 0, 1);
        return result == Simulator.ExecuteResult.Succeeded
            && next.Progress >= craft.CraftProgress
            && next.Quality < craft.CraftQualityMax;
    }

    internal static DonatelloNative.SolveMode ResolveLiveSolveMode(CraftState craft)
        => IsProgressOnly(craft)
            ? DonatelloNative.SolveMode.CompleteFastest
            : DonatelloNative.SolveMode.LiveAdaptive;

    internal static DonatelloNative.SolveMode ResolvePendingSolveMode(CraftState craft, int incumbentActionCount)
        => ProtectsRaphaelBaseline(craft) && incumbentActionCount == 0
            ? DonatelloNative.SolveMode.OptimizeQuality
            : ResolveLiveSolveMode(craft);

    private static bool IsProgressOnly(CraftState craft)
        => craft.DonatelloOptions?.Objective == DonatelloSolveObjective.ProgressOnly;

    private static bool IsProgressRelevantCondition(Condition condition)
        => condition is Condition.Good or Condition.Excellent
            or Condition.Centered or Condition.Sturdy or Condition.Pliant
            or Condition.Malleable or Condition.Primed or Condition.GoodOmen
            or Condition.Robust;

    private static bool IsStrictlyBetter(
        CraftState craft,
        DonatelloPlanEvaluation candidate,
        DonatelloPlanEvaluation incumbent)
    {
        if (!IsProgressOnly(craft))
            return candidate.IsStrictlyBetterThan(incumbent);
        if (candidate.Completes != incumbent.Completes)
            return candidate.Completes;
        if (candidate.Steps != incumbent.Steps)
            return candidate.Steps < incumbent.Steps;
        return candidate.Duration < incumbent.Duration;
    }

    internal static bool ShouldAdoptCandidate(
        CraftState craft,
        DonatelloPlanEvaluation candidate,
        DonatelloPlanEvaluation incumbent,
        bool stagedProgressPlan)
    {
        if (!candidate.Completes)
            return false;
        if (ProtectsRaphaelBaseline(craft))
            return candidate.IsStrictlyBetterThan(incumbent);
        return stagedProgressPlan || IsStrictlyBetter(craft, candidate, incumbent);
    }

    internal static bool ProtectsRaphaelBaseline(CraftState craft)
        => !IsProgressOnly(craft);

    private static string Fingerprint(StepState step)
        => $"{step.Index}/{step.Progress}/{step.Quality}/{step.Durability}/{step.RemainingCP}/{(int)step.Condition}/"
            + $"{step.IQStacks}/{step.WasteNotLeft}/{step.ManipulationLeft}/{step.GreatStridesLeft}/"
            + $"{step.InnovationLeft}/{step.VenerationLeft}/{step.MuscleMemoryLeft}/{step.FinalAppraisalLeft}/"
            + $"{step.CarefulObservationLeft}/{step.CrafterDelineationsLeft}/{(int)step.ComboAction}/{(int)step.PrevComboAction}/{step.HeartAndSoulActive}/"
            + $"{step.HeartAndSoulAvailable}/{step.ExpedienceLeft}/{step.QuickInnoLeft}/"
            + $"{step.QuickInnoAvailable}/{step.TrainedPerfectionActive}/{step.TrainedPerfectionAvailable}/"
            + $"{step.StellarSteadyHandCharges}/{step.StellarSteadyHandLeft}/{step.StellarSteadyHandsUsed}";

    public override Solver Clone()
        => new DonatelloSolver(
            new CachedRaphaelSolution { ActionIds = _plan.Select(action => (uint)action).ToList() },
            _craft);
}
