using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
            GatherBuddy.Log.Debug("[Donatello] Initial Raphael plan already reaches maximum quality; using protected incumbent with concurrent opportunistic replans");
            return new DonatelloProtectedRaphaelSolver(solution, craft);
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

internal sealed class DonatelloProtectedRaphaelSolver : DonatelloSolver, IDonatelloRaphaelIncumbent
{
    public DonatelloProtectedRaphaelSolver(CachedRaphaelSolution solution, CraftState craft)
        : base(solution, craft, protectedMaxQuality: true)
    {
    }

    IReadOnlyList<VulcanSkill> IDonatelloRaphaelIncumbent.RemainingActions => RemainingActions;
}

public class DonatelloSolver : Solver, IDisposable
{
    internal const int DefaultLiveReplanDeadlineMillis = 2000;
    internal const int ProtectedRaphaelTakeoverDeadlineMillis = 30_000;
    internal const int DefaultImprovementQuietPeriodSeconds = 5;
    internal const int MinimumImprovementQuietPeriodSeconds = 1;
    internal const int MaximumImprovementQuietPeriodSeconds = 30;
    internal const int DefaultImprovementQuietPeriodMillis = DefaultImprovementQuietPeriodSeconds * 1000;
    internal const int MinimumImprovementQuietPeriodMillis = 1;
    internal const int MaximumImprovementQuietPeriodMillis = 30_000;
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
    private string? _maximumQualityGuardRoot;
    private readonly bool _protectedRaphaelTakeover;
    private readonly bool _protectedMaxQualityPlan;
    private bool _opportunisticPending;
    private bool _pendingResultInvalidatedByIssuedAction;
    private bool _pendingUsesImprovementQuiescence;
    internal int NativeReplanCount { get; private set; }
    internal bool HasPendingOpportunisticReplan
        => _opportunisticPending && _pendingSolve is { IsCompleted: false };
    internal IReadOnlyList<VulcanSkill> RemainingActions => _plan.Skip(_actionIndex).ToList();
    private readonly object _interruptLock = new();
    private bool _disposed;
    private readonly ProgressOnlySolver _progressOnlySolver = new();

    public DonatelloSolver(CachedRaphaelSolution initialSolution, CraftState craft)
        : this(initialSolution, craft, protectedMaxQuality: false)
    {
    }

    internal DonatelloSolver(CachedRaphaelSolution initialSolution, CraftState craft, bool protectedMaxQuality)
    {
        _craft = craft ?? throw new ArgumentNullException(nameof(craft));
        _plan = initialSolution?.ActionIds.ConvertAll(id => (VulcanSkill)id)
            ?? throw new ArgumentNullException(nameof(initialSolution));
        _protectedMaxQualityPlan = protectedMaxQuality;
        _needsInitialStagedReplan = !protectedMaxQuality && !craft.CraftExpert && !IsProgressOnly(craft);
    }

    internal DonatelloSolver(CraftState craft)
    {
        _craft = craft ?? throw new ArgumentNullException(nameof(craft));
        _plan = [];
    }

    internal DonatelloSolver(CraftState craft, IReadOnlyList<VulcanSkill> incumbent)
    {
        _craft = craft ?? throw new ArgumentNullException(nameof(craft));
        _plan = incumbent?.ToList() ?? throw new ArgumentNullException(nameof(incumbent));
        _protectedRaphaelTakeover = true;
        _protectedMaxQualityPlan = true;
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
            var sameRoot = rootMatchesPending(step);
            if (!_pendingSolve.IsCompleted)
            {
                if (!sameRoot
                    && (_opportunisticPending
                        || _protectedMaxQualityPlan
                        || _pendingUsesImprovementQuiescence))
                {
                    DiscardPendingReplan();
                }
                else
                {
                    var timeout = TimeSpan.FromMinutes(
                        GatherBuddy.Config.RaphaelSolverConfig.RaphaelTimeoutMinutes);
                    if (!_pendingUsesImprovementQuiescence
                        && !_interruptRequested
                        && DateTime.UtcNow - _pendingStartedAt >= timeout)
                    {
                        _interruptRequested = true;
                        RequestInterrupt();
                        GatherBuddy.Log.Warning("[Donatello] Replan timed out; interrupting native search and retaining incumbent");
                    }
                    return new(VulcanSkill.None, "Donatello re-optimizing remaining craft");
                }
            }
            else if (_pendingResultInvalidatedByIssuedAction
                || _opportunisticPending && !sameRoot)
            {
                DiscardPendingReplan();
            }
            else
            {
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
        if (_maximumQualityGuardRoot != null
            && !string.Equals(_maximumQualityGuardRoot, rootKey, StringComparison.Ordinal))
            _maximumQualityGuardRoot = null;
        if (_needsInitialStagedReplan || _replanAtProgressBoundary)
        {
            _needsInitialStagedReplan = false;
            _replanAtProgressBoundary = false;
            StartReplan(step, rootKey, opportunistic: false);
            return new(VulcanSkill.None, "Donatello preparing staged progress plan");
        }

        var progressConditionReaction = !IsProgressOnly(_craft)
            && !_craft.CraftExpert
            && step.Progress < _craft.CraftProgress - 1
            && step.Quality < _craft.CraftQualityMax
            && step.Condition is Condition.Good or Condition.Excellent;
        var poorCarefulObservationReaction = ShouldPlanCarefulObservation(_craft, step);
        var protectedConditionReplan = _protectedMaxQualityPlan
            && !IsProgressOnly(_craft)
            && step.Condition != Condition.Normal
            && (IsProtectedQualityRecoveryCondition(step.Condition)
                || ResolveProtectedOpportunisticDeadlineMillis(GatherBuddy.Config.VulcanExecutionDelayMs) > 0);
        if ((RequiresReplan(_craft, _expectedState, step)
                || progressConditionReaction
                || poorCarefulObservationReaction
                || protectedConditionReplan)
            && rootKey != _handledRoot)
        {
            if (CanRepresentLiveRoot(step, out var unsupportedReason))
            {
                var opportunistic = CanStartOpportunisticProtectedReplan(step);
                StartReplan(step, rootKey, opportunistic);
                if (opportunistic)
                    return CommitCurrentAction(step, "Donatello opportunistic replan overlapping action delay");
                return new(VulcanSkill.None, "Donatello re-optimizing remaining craft");
            }
            _handledRoot = rootKey;
            GatherBuddy.Log.Warning($"[Donatello] Replan skipped; retaining incumbent: {unsupportedReason}");
        }

        if (_actionIndex >= _plan.Count)
            return new(VulcanSkill.None, "Donatello plan exhausted before craft completion", IsTerminalFailure: true);

        var action = _plan[_actionIndex];
        var shouldReplanBeforeCompletion = ShouldReplanBeforeCompletion(_craft, step, action);
        var shouldReplanAfterMaximumQuality = ShouldReplanAfterMaximumQuality(_craft, step, action);
        if (shouldReplanAfterMaximumQuality)
        {
            if (string.Equals(_maximumQualityGuardRoot, rootKey, StringComparison.Ordinal))
            {
                _maximumQualityGuardRoot = null;
                ActivateCompletionFallback(
                    "maximum quality replan retained a quality-only action at the live root");
                return FailureOrCompletionFallback(_craft, step);
            }

            if (rootKey != _handledRoot)
            {
                if (!CanRepresentLiveRoot(step, out var unsupportedReason))
                {
                    _maximumQualityGuardRoot = null;
                    ActivateCompletionFallback(
                        $"cannot safely replan after maximum quality: {unsupportedReason}");
                    return FailureOrCompletionFallback(_craft, step);
                }

                _maximumQualityGuardRoot = rootKey;
                StartReplan(step, rootKey, opportunistic: false);
                return new(VulcanSkill.None, "Donatello re-optimizing after maximum quality");
            }
        }
        else if (string.Equals(_maximumQualityGuardRoot, rootKey, StringComparison.Ordinal))
        {
            _maximumQualityGuardRoot = null;
        }

        if (rootKey != _handledRoot && shouldReplanBeforeCompletion)
        {
            if (CanRepresentLiveRoot(step, out var unsupportedReason))
            {
                StartReplan(step, rootKey, opportunistic: false);
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

    internal async Task<Recommendation> SolveUntilReadyAsync(
        CraftState craft,
        StepState step,
        bool resume,
        CancellationToken cancellationToken = default)
    {
        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
                return new(VulcanSkill.None, "Donatello solve superseded by newer observed state");

            var recommendation = resume ? ResumeFromLiveState(step) : Solve(craft, step);
            resume = false;
            var pending = _pendingSolve;
            if (recommendation.Action != VulcanSkill.None || pending == null)
                return recommendation;

            using var cancellationRegistration = cancellationToken.Register(RequestInterrupt);
            try
            {
                await pending.ConfigureAwait(false);
            }
            catch
            {
                // Solve() consumes the completed task and applies the validated-incumbent fallback.
            }
            if (cancellationToken.IsCancellationRequested)
            {
                DiscardPendingReplan();
                return new(VulcanSkill.None, "Donatello solve superseded by newer observed state");
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
        StartReplan(step, rootKey, opportunistic: false);
        return new(VulcanSkill.None, "Donatello re-optimizing resumed craft from live state");
    }

    private void StartReplan(StepState root, string rootKey, bool opportunistic)
    {
        NativeReplanCount++;
        var liveRoot = root with { };
        var incumbent = _plan.Skip(_actionIndex).ToList();
        _pendingRoot = liveRoot;
        _pendingIncumbent = incumbent;
        _handledRoot = rootKey;
        _pendingStartedAt = DateTime.UtcNow;
        _interruptRequested = false;
        _opportunisticPending = opportunistic;
        _pendingResultInvalidatedByIssuedAction = false;
        IntPtr interrupt;
        lock (_interruptLock)
        {
            if (_disposed)
                return;
            _pendingInterrupt = DonatelloNative.CreateInterrupt();
            interrupt = _pendingInterrupt;
        }
        var solveMode = ResolvePendingSolveMode(_craft, incumbent.Count);
        _pendingUsesImprovementQuiescence = UsesImprovementQuiescence(_craft)
            && solveMode != DonatelloNative.SolveMode.CompleteFastest;
        _pendingEstablishesBaseline = solveMode == DonatelloNative.SolveMode.OptimizeQuality
            && ProtectsRaphaelBaseline(_craft)
            && incumbent.Count == 0;
        var raphaelConfig = GatherBuddy.Config.RaphaelSolverConfig;
        var actionDelayMillis = GatherBuddy.Config.VulcanExecutionDelayMs;
        var recoverySearch = IsProtectedQualityRecoveryCondition(root.Condition);
        var forcedDeadline = _craft.DonatelloOptions?.ReplanDeadlineMillis;
        var softDeadlineMillis = _pendingUsesImprovementQuiescence
            ? ResolveImprovementQuietPeriodMillis(
                _craft,
                raphaelConfig.DonatelloImprovementQuietSeconds)
            : recoverySearch
            ? forcedDeadline is > 0
                ? Math.Clamp(forcedDeadline.Value, 1, ProtectedRaphaelTakeoverDeadlineMillis)
                : ProtectedRaphaelTakeoverDeadlineMillis
            : forcedDeadline is > 0
                ? Math.Clamp(forcedDeadline.Value, 1, 30_000)
                : _pendingEstablishesBaseline
                    ? Math.Clamp(raphaelConfig.RaphaelInitialOptimizationSeconds, 1, 300) * 1000
                    : _protectedMaxQualityPlan || _protectedRaphaelTakeover
                        ? ResolveProtectedOpportunisticDeadlineMillis(actionDelayMillis)
                        : ResolveLiveReplanDeadlineMillis(
                            _craft,
                            raphaelConfig.DonatelloOptimizationThresholdMs,
                            actionDelayMillis);
        var hardDeadlineMillis = _pendingUsesImprovementQuiescence
            ? 0
            : recoverySearch
            ? forcedDeadline is > 0
                ? Math.Clamp(forcedDeadline.Value, 1, ProtectedRaphaelTakeoverDeadlineMillis)
                : ProtectedRaphaelTakeoverDeadlineMillis
            : forcedDeadline is > 0
                ? Math.Clamp(forcedDeadline.Value, 1, 30_000)
                : _pendingEstablishesBaseline
                    ? Math.Clamp(raphaelConfig.RaphaelTimeoutMinutes, 1, 60) * 60 * 1000
                    : softDeadlineMillis;
        var minimizeSteps = _protectedMaxQualityPlan || _protectedRaphaelTakeover ? true : (bool?)null;
        if (_pendingUsesImprovementQuiescence)
        {
            GatherBuddy.Log.Information(
                $"[Donatello] Searching until {softDeadlineMillis}ms pass without a strict improvement; "
                + "retaining the same native search frontier with no hard deadline.");
        }
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
                    hardDeadlineMillis: hardDeadlineMillis,
                    minimizeSteps: minimizeSteps);
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
        => UsesImprovementQuiescence(craft)
            ? ResolveImprovementQuietPeriodMillis(
                craft,
                DefaultImprovementQuietPeriodSeconds)
            : Math.Max(
                Math.Clamp(configuredDeadlineMillis, 10, 10_000),
                Math.Clamp(actionDelayMillis, 0, 10_000));

    internal static bool UsesImprovementQuiescence(CraftState craft)
        => craft.DonatelloOptions is
        {
            Objective: DonatelloSolveObjective.MaximizeQuality,
            MaximizeQualityAtCostOfTime: true,
        };

    internal static int ResolveImprovementQuietPeriodMillis(
        CraftState craft,
        int configuredSeconds)
        => Math.Clamp(
            craft.DonatelloOptions?.ImprovementQuietPeriodMillis
                ?? Math.Clamp(
                    configuredSeconds,
                    MinimumImprovementQuietPeriodSeconds,
                    MaximumImprovementQuietPeriodSeconds) * 1000,
            MinimumImprovementQuietPeriodMillis,
            MaximumImprovementQuietPeriodMillis);

    internal static bool ShouldUseCarefulObservation(CraftState craft, StepState step)
    {
        var options = craft.DonatelloOptions;
        if (options?.MaximizeQualityAtCostOfTime != true
            || options.Objective != DonatelloSolveObjective.MaximizeQuality
            || !craft.Specialist
            || !CraftingContextResolver.ResolveSpecialistActionsAllowed(craft)
            || step.Condition != Condition.Normal
            || step.ComboAction != VulcanSkill.None
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
            && step.ComboAction == VulcanSkill.None
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
        ClearPendingSlots();
    }

    internal bool TryApplyCompletedOpportunisticReplan(StepState step, out Recommendation recommendation)
    {
        recommendation = default;
        if (!_opportunisticPending || _pendingSolve == null || !_pendingSolve.IsCompleted)
            return false;
        if (!rootMatchesPending(step))
        {
            DiscardPendingReplan();
            return false;
        }

        CompleteReplan();
        if (_protectedQualityFailure != null)
        {
            recommendation = new(VulcanSkill.None, _protectedQualityFailure, IsTerminalFailure: true);
            return true;
        }

        recommendation = CommitCurrentAction(step, "Donatello adopted opportunistic improvement");
        return recommendation.Action != VulcanSkill.None;
    }

    internal void NotifyOpportunisticActionIssued()
    {
        if (!_opportunisticPending && _pendingSolve == null)
            return;
        _pendingResultInvalidatedByIssuedAction = _pendingSolve != null;
        _opportunisticPending = false;
        if (_pendingSolve != null && !_pendingSolve.IsCompleted)
            RequestInterrupt();
    }

    private Recommendation CommitCurrentAction(StepState step, string comment)
    {
        if (_actionIndex >= _plan.Count)
            return new(VulcanSkill.None, "Donatello plan exhausted before craft completion", IsTerminalFailure: true);

        var action = _plan[_actionIndex];
        var (result, expected) = Simulator.Execute(_craft, step, action, 0, 1);
        if (result == Simulator.ExecuteResult.CantUse)
        {
            ActivateCompletionFallback($"refused unusable planned action {action}");
            return FailureOrCompletionFallback(_craft, step);
        }

        _actionIndex++;
        _expectedState = expected;
        if (_progressBoundaryActionCount is int progressBoundary
            && _actionIndex == progressBoundary)
            _replanAtProgressBoundary = true;
        return new(action, comment);
    }

    private bool CanStartOpportunisticProtectedReplan(StepState step)
        => _protectedMaxQualityPlan
            && !IsProtectedQualityRecoveryCondition(step.Condition)
            && ResolveProtectedOpportunisticDeadlineMillis(GatherBuddy.Config.VulcanExecutionDelayMs) > 0;

    internal static bool IsProtectedQualityRecoveryCondition(Condition condition)
        => condition is Condition.Excellent or Condition.Poor;

    internal static int ResolveProtectedOpportunisticDeadlineMillis(int actionDelayMillis)
        => Math.Clamp(actionDelayMillis, 0, 10_000);

    private bool rootMatchesPending(StepState step)
        => string.Equals(_handledRoot, Fingerprint(step), StringComparison.Ordinal);

    private void DiscardPendingReplan()
    {
        if (_pendingSolve != null && !_pendingSolve.IsCompleted)
            RequestInterrupt();
        try
        {
            _pendingSolve?.GetAwaiter().GetResult();
        }
        catch
        {
            // Interrupted opportunistic searches are discarded, not admitted.
        }

        ClearPendingSlots();
    }

    private void ClearPendingSlots()
    {
        _pendingSolve = null;
        _pendingRoot = null;
        _pendingIncumbent = null;
        _pendingEstablishesBaseline = false;
        _pendingInterrupt = IntPtr.Zero;
        _interruptRequested = false;
        _opportunisticPending = false;
        _pendingResultInvalidatedByIssuedAction = false;
        _pendingUsesImprovementQuiescence = false;
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
            _maximumQualityGuardRoot = null;
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

    internal static bool ShouldReplanAfterMaximumQuality(CraftState craft, StepState step, VulcanSkill action)
    {
        if (IsProgressOnly(craft)
            || craft.CraftExpert
            || craft.IsCosmic
            || step.Quality < craft.CraftQualityMax
            || Simulator.GetSuccessRate(step, action) < 1.0
            || !Simulator.CanUseAction(craft, step, action))
            return false;

        return Simulator.CalculateProgress(craft, step, action) == 0
            && Simulator.CalculateQuality(craft, step, action) > 0;
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
    {
        var solution = new CachedRaphaelSolution
        {
            ActionIds = _plan.Select(action => (uint)action).ToList(),
        };
        return _protectedMaxQualityPlan
            ? new DonatelloProtectedRaphaelSolver(solution, _craft)
            : new DonatelloSolver(solution, _craft);
    }
}
