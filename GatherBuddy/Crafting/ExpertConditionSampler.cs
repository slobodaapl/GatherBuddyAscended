using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GatherBuddy.Automation;
using Lumina.Excel.Sheets;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace GatherBuddy.Crafting;

internal readonly record struct ExpertConditionTransitionCount(
    byte SourceCondition,
    byte DestinationCondition,
    int Count);

internal sealed class ExpertConditionTransitionMatrix
{
    private readonly Dictionary<(byte Source, byte Destination), int> _counts = [];

    public int Total { get; private set; }

    public void Add(byte source, byte destination)
    {
        _counts[(source, destination)] = _counts.GetValueOrDefault((source, destination)) + 1;
        ++Total;
    }

    public int GetCount(byte source, byte destination)
        => _counts.GetValueOrDefault((source, destination));

    public ExpertConditionTransitionCount[] GetRows()
        => _counts
            .OrderBy(entry => entry.Key.Source)
            .ThenBy(entry => entry.Key.Destination)
            .Select(entry => new ExpertConditionTransitionCount(
                entry.Key.Source,
                entry.Key.Destination,
                entry.Value))
            .ToArray();

    public void Clear()
    {
        _counts.Clear();
        Total = 0;
    }
}

internal sealed class ExpertConditionSamplingRunBudget(int target)
{
    public int Target { get; } = target;
    public int Completed { get; private set; }

    public bool RecordCompletedRun()
    {
        if (Completed >= Target)
            return false;
        ++Completed;
        return Completed < Target;
    }

    public void Reset()
        => Completed = 0;
}

internal readonly record struct ExpertConditionSnapshot(
    DateTime ObservedAtUtc,
    uint RecipeId,
    string RecipeName,
    ushort RecipeLevelTableId,
    ushort ConditionsFlag,
    byte Stars,
    ushort Step,
    byte ConditionId,
    string ConditionName,
    bool AllowedByConditionsFlag,
    int CurrentCp,
    int Progress,
    int Quality,
    int Durability,
    uint StateFlags,
    byte CraftFlags);

/// <summary>
/// Hidden research mode. It samples live expert Trial Synthesis condition transitions using Observe.
/// Captured transitions are never canonicalized or filtered; deterministic edges are identified later.
/// </summary>
public static unsafe class ExpertConditionSampler
{
    private enum RestartPhase
    {
        None,
        ClickQuit,
        ConfirmQuit,
        WaitTrialClose,
        OpenRecipe,
        WaitRecipe,
        ConfirmTrialStart,
        WaitTrialStart,
    }

    private sealed record PendingObserve(
        ExpertConditionSnapshot Source,
        uint ActionId,
        DateTime IssuedAtUtc);

    private static readonly ExpertConditionTransitionMatrix RunMatrix = new();
    private static readonly ExpertConditionTransitionMatrix SessionMatrix = new();
    private static readonly ExpertConditionSamplingRunBudget RunBudget = new(100);
    private static readonly Guid RunId = Guid.NewGuid();
    private static readonly TimeSpan ActionTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RestartPhaseTimeout = TimeSpan.FromSeconds(15);

    private static StreamWriter? _writer;
    private static string? _outputPath;
    private static ExpertConditionSnapshot? _current;
    private static PendingObserve? _pending;
    private static DateTime _nextIssueAtUtc;
    private static DateTime? _actionUnavailableSinceUtc;
    private static int _sessionId;
    private static long _sequence;
    private static bool _active;
    private static bool _halted;
    private static bool _sessionStartedAtInitialStep;
    private static bool _restartFailureLatched;
    private static string _haltReason = string.Empty;
    private static bool _queueBlockReported;
    private static bool _autosolveBlockReported;
    private static RestartPhase _restartPhase;
    private static DateTime _restartPhaseStartedUtc;
    private static DateTime _nextRestartActionUtc;
    private static uint _restartRecipeId;
    private static ushort _restartRecipeLevelTableId;
    private static ushort _restartConditionsFlag;

    public static bool Update()
    {
        if (!GatherBuddy.Config.ExpertConditionSamplingEnabled)
        {
            if (_active || _writer != null || _restartPhase != RestartPhase.None || _restartFailureLatched)
                Shutdown("disabled");
            return false;
        }

        if (CraftingGatherBridge.HasActiveQueue)
        {
            if (_restartPhase != RestartPhase.None)
                FailAutomaticRestart("A crafting queue became active during automatic Trial Synthesis restart");
            EndSession("crafting-queue-active");
            if (!_queueBlockReported)
            {
                _queueBlockReported = true;
                GatherBuddy.Log.Error("[ExpertConditionSampler] Refusing to sample while a crafting queue is active");
            }
            return false;
        }

        _queueBlockReported = false;

        if (CraftingGameInterop.HasOwnedCraft)
        {
            EndSession("crafting-autosolve-active");
            if (!_autosolveBlockReported)
            {
                _autosolveBlockReported = true;
                GatherBuddy.Log.Error("[ExpertConditionSampler] Refusing to sample while crafting autosolve owns a craft");
            }
            return false;
        }

        _autosolveBlockReported = false;

        if (_restartPhase != RestartPhase.None)
            return UpdateAutomaticRestart();

        if (!TryGetExpertTrialHandler(out var handler, out var recipe))
        {
            EndSession("trial-closed-or-not-expert");
            if (!_restartFailureLatched
                && RunBudget.Completed < RunBudget.Target
                && TryScheduleSelectedExpertTrialStart())
                return true;
            return false;
        }

        // Claim the expert trial before reading UI state. A transiently incomplete frame must not fall
        // through to the normal crafting solver or queue path.
        if (!TryReadStableSnapshot(handler, recipe, out var snapshot))
            return true;

        if (_restartFailureLatched)
        {
            _current = snapshot;
            return true;
        }

        if (!_active
            || (_current is { } current
                && (snapshot.RecipeId != current.RecipeId
                    || snapshot.RecipeLevelTableId != current.RecipeLevelTableId
                    || snapshot.ConditionsFlag != current.ConditionsFlag
                    || snapshot.Step < current.Step)))
        {
            EndSession("new-trial");
            if (RunBudget.Completed >= RunBudget.Target)
            {
                _halted = true;
                _haltReason = $"target reached ({RunBudget.Completed}/{RunBudget.Target} Trial sessions)";
                _current = snapshot;
                return true;
            }
            StartSession(snapshot);
            return true;
        }

        if (_current == null)
        {
            StartSession(snapshot);
            return true;
        }

        if (IsActionExecuting(snapshot))
            return true;

        if (_pending is { } pending)
        {
            if (snapshot.Step == pending.Source.Step)
            {
                if (DateTime.UtcNow - pending.IssuedAtUtc >= ActionTimeout)
                    Halt("Observe timed out without a step transition", "action-timeout", snapshot);
                return true;
            }

            CaptureTransition(pending.Source, snapshot, pending.ActionId, pending.IssuedAtUtc);
            _pending = null;
            _current = snapshot;
            _nextIssueAtUtc = DateTime.UtcNow.AddMilliseconds(100);
            return true;
        }

        if (snapshot.Step != _current.Value.Step)
        {
            CaptureTransition(_current.Value, snapshot, 0, null);
            _current = snapshot;
            _nextIssueAtUtc = DateTime.UtcNow.AddMilliseconds(100);
            return true;
        }

        if (!_halted && DateTime.UtcNow >= _nextIssueAtUtc)
            TryIssueObserve(snapshot);

        return true;
    }

    public static void Disable()
        => Shutdown("disabled-by-command");

    public static string Enable()
    {
        if (_restartFailureLatched && _restartRecipeId != 0)
        {
            _restartFailureLatched = false;
            _halted = false;
            _haltReason = string.Empty;
            SetRestartPhase(TryGetVisibleSynthesis(out _) ? RestartPhase.ClickQuit : RestartPhase.OpenRecipe, 250);
            GatherBuddy.Log.Information($"[ExpertConditionSampler] Resuming automatic restart for recipe {_restartRecipeId}");
            return $"[ExpertConditionSampler] Resuming automatic restart for recipe {_restartRecipeId}.";
        }

        if (_active || _restartPhase != RestartPhase.None)
            return "[ExpertConditionSampler] Already active.";

        if (TryScheduleSelectedExpertTrialStart())
            return $"[ExpertConditionSampler] Enabled; starting selected expert Trial Synthesis for recipe {_restartRecipeId}.";

        return "[ExpertConditionSampler] Enabled. Open an expert Trial Synthesis; it will sample and restart automatically for up to 100 complete runs.";
    }

    public static void Dispose()
        => Shutdown("plugin-dispose");

    public static string GetStatus()
    {
        var state = _restartPhase != RestartPhase.None
            ? $"restarting:{_restartPhase}"
            : _halted
                ? $"halted: {_haltReason}"
                : _active ? "sampling" : "idle";
        var craft = _current is { } current
            ? $" recipe={current.RecipeId} rlt={current.RecipeLevelTableId} step={current.Step}"
            : string.Empty;
        return $"[ExpertConditionSampler] enabled={GatherBuddy.Config.ExpertConditionSamplingEnabled} state={state}{craft} runs={RunBudget.Completed}/{RunBudget.Target} transitions={RunMatrix.Total} output={_outputPath ?? "not-created"}";
    }

    internal static bool IsAllowedByConditionsFlag(ushort conditionsFlag, byte conditionId)
        => conditionId is >= 1 and <= 16
            && (conditionsFlag & (1 << (conditionId - 1))) != 0;

    internal static string GetConditionName(byte conditionId)
        => conditionId switch
        {
            1 => "Normal",
            2 => "Good",
            3 => "Excellent",
            4 => "Poor",
            5 => "Centered",
            6 => "Sturdy",
            7 => "Pliant",
            8 => "Malleable",
            9 => "Primed",
            10 => "GoodOmen",
            11 => "Robust",
            _ => $"Unknown({conditionId})",
        };

    private static bool TryGetExpertTrialHandler(out CraftEventHandler* handler, out Recipe recipe)
    {
        handler = null;
        recipe = default;
        if (!Dalamud.Conditions[ConditionFlag.Crafting])
            return false;

        var eventFramework = FFXIVClientStructs.FFXIV.Client.Game.Event.EventFramework.Instance();
        handler = eventFramework == null ? null : eventFramework->GetCraftEventHandler();
        if (handler == null
            || handler->RecipeId == 0
            || (handler->CraftFlags & CraftFlags.NotTrialSynthesis) != 0)
            return false;

        var recipes = Dalamud.GameData.GetExcelSheet<Recipe>();
        return recipes != null
            && recipes.TryGetRow(handler->RecipeId, out recipe)
            && recipe.IsExpert
            && handler->ConditionsFlag != 15;
    }

    private static bool TryReadStableSnapshot(
        CraftEventHandler* handler,
        Recipe recipe,
        out ExpertConditionSnapshot snapshot)
    {
        snapshot = default;
        var synthesis = SynthesisReader.GetSynthesisAddon();
        if (synthesis == null || !synthesis->IsVisible || handler->StepNumber == 0)
            return false;

        var uiStep = SynthesisReader.GetStepIndex(synthesis);
        var conditionId = (byte)handler->Condition;
        var uiConditionId = (int)SynthesisReader.GetCondition(synthesis) + 1;
        if (uiStep != handler->StepNumber || uiConditionId != conditionId)
            return false;

        snapshot = new ExpertConditionSnapshot(
            DateTime.UtcNow,
            handler->RecipeId,
            recipe.ItemResult.Value.Name.ExtractText(),
            handler->RecipeLevelTable,
            handler->ConditionsFlag,
            handler->Stars,
            handler->StepNumber,
            conditionId,
            GetConditionName(conditionId),
            IsAllowedByConditionsFlag(handler->ConditionsFlag, conditionId),
            (int)(Dalamud.Objects.LocalPlayer?.CurrentCp ?? 0),
            SynthesisReader.GetProgress(synthesis),
            SynthesisReader.GetQuality(synthesis),
            SynthesisReader.GetDurability(synthesis),
            (uint)handler->StateFlags,
            (byte)handler->CraftFlags);
        return true;
    }

    private static bool IsActionExecuting(ExpertConditionSnapshot snapshot)
        => Dalamud.Conditions[ConditionFlag.ExecutingCraftingAction]
            || (snapshot.CraftFlags & ((byte)CraftFlags.ExecutingAction1 | (byte)CraftFlags.ExecutingAction2)) != 0;

    private static void StartSession(ExpertConditionSnapshot initial)
    {
        _active = true;
        _halted = false;
        _haltReason = string.Empty;
        _pending = null;
        _current = initial;
        _nextIssueAtUtc = DateTime.UtcNow.AddMilliseconds(250);
        _actionUnavailableSinceUtc = null;
        _sessionStartedAtInitialStep = initial.Step == 1;
        SessionMatrix.Clear();
        ++_sessionId;

        if (!EnsureWriter())
        {
            _halted = true;
            _haltReason = "output file unavailable";
            return;
        }

        Write(new
        {
            SchemaVersion = 1,
            Kind = "session-start",
            RunId,
            SessionId = _sessionId,
            Sequence = ++_sequence,
            Initial = initial,
        });

        if (!initial.AllowedByConditionsFlag)
            Halt("Initial condition is absent from the recipe condition flags", "invalid-initial-condition", initial);
        else
            GatherBuddy.Log.Information($"[ExpertConditionSampler] Started recipe={initial.RecipeId} rlt={initial.RecipeLevelTableId} flags={initial.ConditionsFlag} output={_outputPath}");
    }

    private static void CaptureTransition(
        ExpertConditionSnapshot source,
        ExpertConditionSnapshot destination,
        uint actionId,
        DateTime? issuedAtUtc)
    {
        var stepDelta = destination.Step - source.Step;
        var sameCraft = source.RecipeId == destination.RecipeId
            && source.RecipeLevelTableId == destination.RecipeLevelTableId
            && source.ConditionsFlag == destination.ConditionsFlag;
        var observePreservedState = actionId == 0
            || (source.Progress == destination.Progress
                && source.Quality == destination.Quality
                && source.Durability == destination.Durability);
        var usableTransition = sameCraft && stepDelta == 1;

        Write(new
        {
            SchemaVersion = 1,
            Kind = usableTransition ? "transition" : "capture-gap",
            RunId,
            SessionId = _sessionId,
            Sequence = ++_sequence,
            ActionId = actionId,
            ActionIssuedAtUtc = issuedAtUtc,
            Source = source,
            Destination = destination,
            StepDelta = stepDelta,
            SameCraft = sameCraft,
            ObservePreservedProgressQualityDurability = observePreservedState,
            Classification = "unclassified",
        });

        if (!usableTransition)
        {
            Halt("A step gap or craft identity change prevents reconstructing the transition", null, destination);
            return;
        }

        // Keep each source/destination edge distinct. No forced edge is folded into an outcome count.
        RunMatrix.Add(source.ConditionId, destination.ConditionId);
        SessionMatrix.Add(source.ConditionId, destination.ConditionId);

        if (!destination.AllowedByConditionsFlag)
            Halt($"Observed condition {destination.ConditionName} is absent from condition flags {destination.ConditionsFlag}", null, destination);
        else if (!observePreservedState)
            Halt("Observe changed progress, quality, or durability; another action may have executed", null, destination);
    }

    private static void TryIssueObserve(ExpertConditionSnapshot source)
    {
        var playerJobId = (uint)(Dalamud.Objects.LocalPlayer?.ClassJob.RowId ?? 0);
        var actionId = Vulcan.VulcanSkill.Observe.ActionId(playerJobId);
        var actionManager = ActionManager.Instance();
        if (actionId == 0 || actionManager == null)
        {
            Halt("Observe action is unavailable for the current crafter", "action-unavailable", source);
            return;
        }

        var expectedCpCost = source.ConditionId == 7 ? 4 : 7;
        if (source.CurrentCp < expectedCpCost)
        {
            BeginAutomaticRestart(source, expectedCpCost);
            return;
        }

        var actionType = actionId >= 100000 ? ActionType.CraftAction : ActionType.Action;
        var status = actionManager->GetActionStatus(actionType, actionId);
        if (status != 0)
        {
            _actionUnavailableSinceUtc ??= DateTime.UtcNow;
            if (DateTime.UtcNow - _actionUnavailableSinceUtc >= ActionTimeout)
                Halt($"Observe remained unavailable with status {status}", "action-unavailable", source);
            return;
        }

        _actionUnavailableSinceUtc = null;
        if (!actionManager->UseAction(actionType, actionId))
        {
            _nextIssueAtUtc = DateTime.UtcNow.AddMilliseconds(250);
            return;
        }

        var issuedAtUtc = DateTime.UtcNow;
        _pending = new PendingObserve(source, actionId, issuedAtUtc);
        Write(new
        {
            SchemaVersion = 1,
            Kind = "action-issued",
            RunId,
            SessionId = _sessionId,
            Sequence = ++_sequence,
            ActionId = actionId,
            IssuedAtUtc = issuedAtUtc,
            Source = source,
        });
    }

    private static void BeginAutomaticRestart(ExpertConditionSnapshot source, int expectedCpCost)
    {
        // Reloading while an already-exhausted Trial is open must bootstrap the restart loop without
        // claiming that the empty post-reload session supplied a completed sample run.
        var countedRun = _sessionStartedAtInitialStep && SessionMatrix.Total > 0;
        var shouldRestart = !countedRun || RunBudget.RecordCompletedRun();
        Write(new
        {
            SchemaVersion = 1,
            Kind = "cp-exhausted",
            RunId,
            SessionId = _sessionId,
            Sequence = ++_sequence,
            Reason = $"CP exhausted ({source.CurrentCp} < {expectedCpCost})",
            CountedRun = countedRun,
            StartedAtInitialStep = _sessionStartedAtInitialStep,
            TransitionCount = SessionMatrix.Total,
            CompletedRuns = RunBudget.Completed,
            TargetRuns = RunBudget.Target,
            Snapshot = source,
        });

        _pending = null;
        if (!shouldRestart)
        {
            _halted = true;
            _haltReason = $"target reached ({RunBudget.Completed}/{RunBudget.Target} Trial sessions)";
            Write(new
            {
                SchemaVersion = 1,
                Kind = "sampling-target-reached",
                RunId,
                SessionId = _sessionId,
                Sequence = ++_sequence,
                CompletedRuns = RunBudget.Completed,
                TargetRuns = RunBudget.Target,
                Snapshot = source,
            });
            GatherBuddy.Log.Information($"[ExpertConditionSampler] {_haltReason}; leaving final Trial Synthesis open");
            return;
        }

        _restartRecipeId = source.RecipeId;
        _restartRecipeLevelTableId = source.RecipeLevelTableId;
        _restartConditionsFlag = source.ConditionsFlag;
        _halted = false;
        _haltReason = string.Empty;
        SetRestartPhase(RestartPhase.ClickQuit, 500);
        Write(new
        {
            SchemaVersion = 1,
            Kind = "automatic-restart-requested",
            RunId,
            SessionId = _sessionId,
            Sequence = ++_sequence,
            CompletedRuns = RunBudget.Completed,
            TargetRuns = RunBudget.Target,
            RecipeId = _restartRecipeId,
            RecipeLevelTableId = _restartRecipeLevelTableId,
        });
        GatherBuddy.Log.Information(countedRun
            ? $"[ExpertConditionSampler] Completed run {RunBudget.Completed}/{RunBudget.Target}; restarting Trial Synthesis"
            : "[ExpertConditionSampler] Restarting an already-exhausted Trial without counting an empty sampling run");
    }

    private static bool UpdateAutomaticRestart()
    {
        var now = DateTime.UtcNow;
        if (now - _restartPhaseStartedUtc >= RestartPhaseTimeout)
        {
            FailAutomaticRestart($"Phase {_restartPhase} timed out after {RestartPhaseTimeout.TotalSeconds:0} seconds");
            return true;
        }
        if (now < _nextRestartActionUtc)
            return true;

        switch (_restartPhase)
        {
            case RestartPhase.ClickQuit:
                if (!TryGetVisibleSynthesis(out var synthesis))
                {
                    FinishTrialClose();
                    break;
                }
                if (TryGetVisibleSelectYesno(out _))
                {
                    FailAutomaticRestart("A confirmation dialog was already open before clicking Quit");
                    break;
                }
                if (TryClickButton((AtkUnitBase*)synthesis, synthesis->QuitButton))
                    SetRestartPhase(RestartPhase.ConfirmQuit);
                break;

            case RestartPhase.ConfirmQuit:
                if (!TryGetVisibleSynthesis(out _))
                {
                    FinishTrialClose();
                    break;
                }
                if (TryGetVisibleSelectYesno(out var quitConfirmation))
                {
                    new AddonMaster.SelectYesno(quitConfirmation).Yes();
                    SetRestartPhase(RestartPhase.WaitTrialClose);
                }
                break;

            case RestartPhase.WaitTrialClose:
                if (!TryGetVisibleSynthesis(out _))
                    FinishTrialClose();
                break;

            case RestartPhase.OpenRecipe:
                var agent = AgentRecipeNote.Instance();
                if (agent != null)
                {
                    agent->OpenRecipeByRecipeId(_restartRecipeId);
                    SetRestartPhase(RestartPhase.WaitRecipe, 500);
                }
                break;

            case RestartPhase.WaitRecipe:
                if (TryFinishAutomaticRestart())
                    break;
                if (!TryGetVisibleRecipeNote(out _))
                    break;
                if (TryGetVisibleSelectYesno(out _))
                {
                    FailAutomaticRestart("A confirmation dialog was already open before clicking Trial Synthesis");
                    break;
                }
                var selectedRecipe = RecipeNoteExt.GetSelectedRecipeEntry();
                if (selectedRecipe == null || selectedRecipe->RecipeId != _restartRecipeId)
                    break;
                if (TrialSynthesisUi.TryRequestStart(_restartRecipeId))
                {
                    GatherBuddy.Log.Information(
                        $"[ExpertConditionSampler] Requested Trial Synthesis for recipe {_restartRecipeId}");
                    SetRestartPhase(RestartPhase.ConfirmTrialStart);
                }
                break;

            case RestartPhase.ConfirmTrialStart:
                if (TryFinishAutomaticRestart())
                    break;
                if (TrialSynthesisUi.TryConfirmStart(_restartRecipeId))
                {
                    GatherBuddy.Log.Information($"[ExpertConditionSampler] Confirmed Trial Synthesis for recipe {_restartRecipeId}");
                    SetRestartPhase(RestartPhase.WaitTrialStart);
                }
                break;

            case RestartPhase.WaitTrialStart:
                TryFinishAutomaticRestart();
                break;
        }

        return true;
    }

    private static void FinishTrialClose()
    {
        EndSession("automatic-restart");
        SetRestartPhase(RestartPhase.OpenRecipe, 500);
    }

    private static bool TryFinishAutomaticRestart()
    {
        if (!TryGetVisibleSynthesis(out _)
            || !TryGetExpertTrialHandler(out var handler, out _))
            return false;

        if (handler->RecipeId != _restartRecipeId
            || handler->RecipeLevelTable != _restartRecipeLevelTableId
            || handler->ConditionsFlag != _restartConditionsFlag)
        {
            FailAutomaticRestart(
                $"Wrong Trial Synthesis started: recipe={handler->RecipeId}, rlt={handler->RecipeLevelTable}, flags={handler->ConditionsFlag}");
            return true;
        }

        Write(new
        {
            SchemaVersion = 1,
            Kind = "automatic-restart-complete",
            RunId,
            SessionId = _sessionId,
            Sequence = ++_sequence,
            CompletedRuns = RunBudget.Completed,
            TargetRuns = RunBudget.Target,
            RecipeId = _restartRecipeId,
            RecipeLevelTableId = _restartRecipeLevelTableId,
            CompletedAtUtc = DateTime.UtcNow,
        });
        GatherBuddy.Log.Information($"[ExpertConditionSampler] Automatic restart complete; beginning run {RunBudget.Completed + 1}/{RunBudget.Target}");
        _restartPhase = RestartPhase.None;
        _restartFailureLatched = false;
        _restartRecipeId = 0;
        _restartRecipeLevelTableId = 0;
        _restartConditionsFlag = 0;
        _current = null;
        _halted = false;
        _haltReason = string.Empty;
        return true;
    }

    private static void FailAutomaticRestart(string reason)
    {
        Write(new
        {
            SchemaVersion = 1,
            Kind = "automatic-restart-failed",
            RunId,
            SessionId = _sessionId,
            Sequence = ++_sequence,
            FailedAtUtc = DateTime.UtcNow,
            Phase = _restartPhase.ToString(),
            Reason = reason,
            CompletedRuns = RunBudget.Completed,
            TargetRuns = RunBudget.Target,
        });
        _restartPhase = RestartPhase.None;
        _restartFailureLatched = true;
        _halted = true;
        _haltReason = $"automatic restart failed: {reason}";
        _pending = null;
        GatherBuddy.Log.Error($"[ExpertConditionSampler] {_haltReason}");
    }

    private static void SetRestartPhase(RestartPhase phase, int delayMilliseconds = 0)
    {
        _restartPhase = phase;
        _restartPhaseStartedUtc = DateTime.UtcNow;
        _nextRestartActionUtc = _restartPhaseStartedUtc.AddMilliseconds(delayMilliseconds);
    }

    private static bool TryGetVisibleSynthesis(out AddonSynthesis* addon)
    {
        var address = Dalamud.GameGui.GetAddonByName("Synthesis").Address;
        addon = (AddonSynthesis*)address;
        return addon != null && addon->AtkUnitBase.IsVisible;
    }

    private static bool TryGetVisibleRecipeNote(out AddonRecipeNote* addon)
    {
        var address = Dalamud.GameGui.GetAddonByName("RecipeNote").Address;
        addon = (AddonRecipeNote*)address;
        return addon != null && addon->AtkUnitBase.IsVisible;
    }

    private static bool TryGetVisibleSelectYesno(out AddonSelectYesno* addon)
    {
        var address = Dalamud.GameGui.GetAddonByName("SelectYesno").Address;
        addon = (AddonSelectYesno*)address;
        return addon != null && addon->AtkUnitBase.IsVisible && addon->AtkUnitBase.IsReady;
    }

    private static bool TryScheduleSelectedExpertTrialStart()
    {
        if (!TryGetVisibleRecipeNote(out _))
            return false;
        var selectedRecipe = RecipeNoteExt.GetSelectedRecipeEntry();
        if (selectedRecipe == null
            || Dalamud.GameData.GetExcelSheet<Recipe>()?.TryGetRow(selectedRecipe->RecipeId, out var recipe) != true
            || !recipe.IsExpert
            || recipe.RecipeLevelTable.Value.ConditionsFlag == 15)
            return false;

        _restartRecipeId = recipe.RowId;
        _restartRecipeLevelTableId = (ushort)recipe.RecipeLevelTable.RowId;
        _restartConditionsFlag = recipe.RecipeLevelTable.Value.ConditionsFlag;
        _halted = false;
        _haltReason = string.Empty;
        SetRestartPhase(RestartPhase.WaitRecipe, 100);
        GatherBuddy.Log.Information($"[ExpertConditionSampler] Starting selected expert Trial Synthesis for recipe {_restartRecipeId}");
        return true;
    }

    private static bool TryClickButton(AtkUnitBase* addon, AtkComponentButton* button)
    {
        if (addon == null || button == null || !button->IsEnabled)
            return false;
        var ownerNode = button->AtkComponentBase.OwnerNode;
        if (ownerNode == null || !ownerNode->AtkResNode.IsVisible())
            return false;
        var eventPointer = ownerNode->AtkResNode.AtkEventManager.Event;
        if (eventPointer == null)
            return false;
        var atkEvent = (AtkEvent*)eventPointer;
        addon->ReceiveEvent(atkEvent->State.EventType, (int)atkEvent->Param, eventPointer);
        return true;
    }

    private static void Halt(string reason, string? kind, ExpertConditionSnapshot snapshot)
    {
        if (_halted)
            return;
        _halted = true;
        _haltReason = reason;
        _pending = null;
        if (kind != null)
        {
            Write(new
            {
                SchemaVersion = 1,
                Kind = kind,
                RunId,
                SessionId = _sessionId,
                Sequence = ++_sequence,
                Reason = reason,
                Snapshot = snapshot,
            });
        }
        GatherBuddy.Log.Warning($"[ExpertConditionSampler] Halted: {reason}");
    }

    private static void EndSession(string reason)
    {
        if (!_active)
            return;

        Write(new
        {
            SchemaVersion = 1,
            Kind = "session-end",
            RunId,
            SessionId = _sessionId,
            Sequence = ++_sequence,
            EndedAtUtc = DateTime.UtcNow,
            Reason = reason,
            TransitionCount = SessionMatrix.Total,
            TransitionMatrix = SessionMatrix.GetRows(),
        });
        _active = false;
        if (!_restartFailureLatched)
        {
            _halted = false;
            _haltReason = string.Empty;
        }
        _pending = null;
        _current = null;
        _actionUnavailableSinceUtc = null;
        _sessionStartedAtInitialStep = false;
        SessionMatrix.Clear();
    }

    private static bool EnsureWriter()
    {
        if (_writer != null)
            return true;
        try
        {
            var directory = Dalamud.PluginInterface.ConfigDirectory.FullName;
            Directory.CreateDirectory(directory);
            _outputPath = Path.Combine(
                directory,
                $"expert-condition-samples-{DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture)}-{Guid.NewGuid():N}.jsonl");
            _writer = new StreamWriter(_outputPath, append: false) { AutoFlush = true };
            return true;
        }
        catch (Exception exception)
        {
            GatherBuddy.Log.Error($"[ExpertConditionSampler] Could not open output file: {exception}");
            return false;
        }
    }

    private static void Write(object record)
    {
        if (_writer == null)
            return;
        try
        {
            _writer.WriteLine(JsonConvert.SerializeObject(record, Formatting.None));
        }
        catch (Exception exception)
        {
            _halted = true;
            _haltReason = "output write failed";
            GatherBuddy.Log.Error($"[ExpertConditionSampler] Output write failed: {exception}");
        }
    }

    private static void Shutdown(string reason)
    {
        EndSession(reason);
        _writer?.Dispose();
        _writer = null;
        _outputPath = null;
        _pending = null;
        _current = null;
        _active = false;
        _halted = false;
        _haltReason = string.Empty;
        _actionUnavailableSinceUtc = null;
        _sessionStartedAtInitialStep = false;
        _restartFailureLatched = false;
        _restartPhase = RestartPhase.None;
        _restartRecipeId = 0;
        _restartRecipeLevelTableId = 0;
        _restartConditionsFlag = 0;
        RunBudget.Reset();
        RunMatrix.Clear();
        SessionMatrix.Clear();
        _sessionId = 0;
        _sequence = 0;
    }
}
