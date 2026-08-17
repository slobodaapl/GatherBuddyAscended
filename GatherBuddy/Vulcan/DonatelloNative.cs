using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using GatherBuddy.Crafting;
using Newtonsoft.Json;

namespace GatherBuddy.Vulcan;

internal static partial class DonatelloNative
{
    private const string LibraryName = "donatello_ffi.dll";
    internal const uint AbiVersion = 10;
    private static readonly SemaphoreSlim NativeSolveGate = new(1, 1);
    private static readonly JsonSerializerOptions RequestSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint donatello_abi_version();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe IntPtr donatello_solve_json(byte* data, nuint length);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe IntPtr donatello_solve_json_interruptible(byte* data, nuint length, IntPtr interrupt);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr donatello_interrupt_create();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void donatello_interrupt_set(IntPtr interrupt);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void donatello_interrupt_free(IntPtr interrupt);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void donatello_string_free(IntPtr value);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void donatello_cache_set_budget_bytes(nuint bytes);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void donatello_cache_clear();

    private sealed class NativeResponse
    {
        public bool Ok { get; set; }
        public List<uint>? ActionIds { get; set; }
        public bool Optimal { get; set; }
        public bool DeadlineReached { get; set; }
        public int AchievedQuality { get; set; }
        public int QualityUpperBound { get; set; }
        public long ElapsedMillis { get; set; }
        public ProgressBoundary? ProgressBoundary { get; set; }
        public FinalStateSummary? FinalState { get; set; }
        public string? Error { get; set; }
    }

    internal sealed class ProgressBoundary
    {
        public int ActionCount { get; set; }
        public string Target { get; set; } = string.Empty;
    }

    internal sealed class FinalStateSummary
    {
        public int Cp { get; set; }
        public int Durability { get; set; }
        public int Progress { get; set; }
        public int Quality { get; set; }
        public bool Complete { get; set; }
    }

    internal enum SolveMode
    {
        OptimizeQuality = 0,
        CompleteFastest = 1,
        LiveAdaptive = 2,
    }

    internal sealed record SolveResult(
        IReadOnlyList<VulcanSkill> Actions,
        bool Optimal,
        bool DeadlineReached,
        int AchievedQuality,
        int QualityUpperBound,
        long ElapsedMillis,
        ProgressBoundary? ProgressBoundary,
        FinalStateSummary FinalState);

    public static IntPtr CreateInterrupt()
        => donatello_interrupt_create();

    public static void ConfigureCache(int memoryMiB)
        => donatello_cache_set_budget_bytes((nuint)Math.Clamp(memoryMiB, 64, 1024) * 1024u * 1024u);

    public static void ClearCache()
        => donatello_cache_clear();

    public static void Interrupt(IntPtr interrupt)
    {
        if (interrupt != IntPtr.Zero)
            donatello_interrupt_set(interrupt);
    }

    public static void FreeInterrupt(IntPtr interrupt)
    {
        if (interrupt != IntPtr.Zero)
            donatello_interrupt_free(interrupt);
    }

    public static unsafe IReadOnlyList<VulcanSkill> Solve(
        CraftState craft,
        StepState root,
        IntPtr interrupt = default,
        IReadOnlyList<VulcanSkill>? incumbent = null)
        => Solve(
            craft,
            root,
            CraftingContextResolver.ResolveSpecialistActionsAllowed(craft),
            SolveMode.OptimizeQuality,
            interrupt,
            incumbent);

    internal static unsafe IReadOnlyList<VulcanSkill> Solve(
        CraftState craft,
        StepState root,
        bool allowSpecialistActions,
        SolveMode solveMode,
        IntPtr interrupt = default,
        IReadOnlyList<VulcanSkill>? incumbent = null)
        => SolveDetailed(craft, root, allowSpecialistActions, solveMode, interrupt, incumbent).Actions;

    internal static unsafe SolveResult SolveDetailed(
        CraftState craft,
        StepState root,
        bool allowSpecialistActions,
        SolveMode solveMode,
        IntPtr interrupt = default,
        IReadOnlyList<VulcanSkill>? incumbent = null,
        int softDeadlineMillis = 0,
        int hardDeadlineMillis = 0,
        bool bypassSolutionCache = false,
        bool? minimizeSteps = null)
    {
        if (donatello_abi_version() != AbiVersion)
            throw new InvalidOperationException("Unsupported Donatello native ABI version");

        NativeSolveGate.Wait();
        try
        {
            var raphaelConfig = GatherBuddy.Config?.RaphaelSolverConfig;
            ConfigureCache(raphaelConfig?.DonatelloCacheMemoryMiB ?? 512);
            var fallbackMinimizeSteps = minimizeSteps
                ?? raphaelConfig?.DonatelloMinimizeSteps
                ?? false;

            var bytes = Encoding.UTF8.GetBytes(SerializeRequest(
                craft, root, allowSpecialistActions, solveMode, incumbent, fallbackMinimizeSteps,
                softDeadlineMillis, hardDeadlineMillis, bypassSolutionCache,
                raphaelConfig?.DonatelloExperimentalProgressPriority == true));
            IntPtr nativeResponse;
            fixed (byte* data = bytes)
                nativeResponse = interrupt == IntPtr.Zero
                    ? donatello_solve_json(data, (nuint)bytes.Length)
                    : donatello_solve_json_interruptible(data, (nuint)bytes.Length, interrupt);
            if (nativeResponse == IntPtr.Zero)
                throw new InvalidOperationException("Donatello native solver returned null");
            try
            {
                var json = Marshal.PtrToStringUTF8(nativeResponse)
                    ?? throw new InvalidOperationException("Donatello native response was not UTF-8");
                var response = JsonConvert.DeserializeObject<NativeResponse>(json)
                    ?? throw new InvalidOperationException("Donatello native response was empty");
                if (!response.Ok || response.ActionIds == null)
                    throw new InvalidOperationException(response.Error ?? "Donatello solve failed");
                var invalidActionId = response.ActionIds.FirstOrDefault(
                    id => !((VulcanSkill)id).IsExecutableAction());
                if (invalidActionId != 0)
                    throw new InvalidOperationException($"Donatello native response contained invalid action ID {invalidActionId}");
                return new SolveResult(
                    response.ActionIds.ConvertAll(id => (VulcanSkill)id),
                    response.Optimal,
                    response.DeadlineReached,
                    response.AchievedQuality,
                    response.QualityUpperBound,
                    response.ElapsedMillis,
                    response.ProgressBoundary,
                    response.FinalState
                        ?? throw new InvalidOperationException("Donatello native response omitted final state"));
            }
            finally
            {
                donatello_string_free(nativeResponse);
            }
        }
        finally
        {
            NativeSolveGate.Release();
        }
    }

    internal static string SerializeRequest(
        CraftState craft,
        StepState root,
        bool allowSpecialistActions,
        SolveMode solveMode,
        IReadOnlyList<VulcanSkill>? incumbent = null,
        bool fallbackMinimizeSteps = false,
        int softDeadlineMillis = 0,
        int hardDeadlineMillis = 0,
        bool bypassSolutionCache = false,
        bool experimentalProgressPriorityEnabled = false)
    {
        var maxStellarSteadyHandUses = craft.DonatelloOptions?.MaxStellarSteadyHandUses ?? 0;
        var remainingStellarSteadyHandUses = maxStellarSteadyHandUses > root.StellarSteadyHandsUsed
            ? maxStellarSteadyHandUses - root.StellarSteadyHandsUsed
            : 0;
        var stellarSteadyHandCharges = Math.Min(
            root.StellarSteadyHandCharges,
            remainingStellarSteadyHandUses);
        var request = new
        {
            AbiVersion,
            MaxCp = craft.StatCP,
            MaxDurability = craft.CraftDurability,
            MaxProgress = craft.CraftProgress,
            MaxQuality = solveMode == SolveMode.CompleteFastest
                ? 0
                : craft.CraftQualityMax,
            BaseProgress = Simulator.BaseProgress(craft),
            BaseQuality = Simulator.BaseQuality(craft),
            JobLevel = craft.StatLevel,
            Manipulation = craft.UnlockedManipulation,
            Specialist = craft.Specialist && allowSpecialistActions,
            AllowCarefulObservation = solveMode != SolveMode.CompleteFastest
                && craft.Specialist
                && allowSpecialistActions
                && craft.DonatelloOptions?.MaximizeQualityAtCostOfTime == true
                && root.Condition == Condition.Poor,
            SolveMode = (int)solveMode,
            ProgressFirst = ResolveProgressFirst(
                craft,
                solveMode,
                experimentalProgressPriorityEnabled),
            MinimizeSteps = solveMode == SolveMode.CompleteFastest
                ? false
                : craft.DonatelloOptions?.MinimizeSteps ?? fallbackMinimizeSteps,
            StellarSteadyHandCharges = stellarSteadyHandCharges,
            IncumbentActionIds = incumbent?.Select(action => (uint)action).ToArray() ?? [],
            SoftDeadlineMillis = Math.Max(0, softDeadlineMillis),
            HardDeadlineMillis = Math.Max(0, hardDeadlineMillis),
            BypassSolutionCache = bypassSolutionCache,
            Root = new
            {
                Cp = root.RemainingCP,
                root.Durability,
                root.Progress,
                Quality = solveMode == SolveMode.CompleteFastest
                    ? 0
                    : root.Quality,
                InnerQuiet = root.IQStacks,
                WasteNot = root.WasteNotLeft,
                Manipulation = root.ManipulationLeft,
                Innovation = root.InnovationLeft,
                Veneration = root.VenerationLeft,
                GreatStrides = root.GreatStridesLeft,
                MuscleMemory = root.MuscleMemoryLeft,
                FinalAppraisal = root.FinalAppraisalLeft,
                CarefulObservationCharges = root.CarefulObservationLeft,
                Combo = Combo(root),
                root.HeartAndSoulActive,
                root.HeartAndSoulAvailable,
                QuickInnovationAvailable = root.QuickInnoLeft > 0,
                root.TrainedPerfectionActive,
                root.TrainedPerfectionAvailable,
                StellarSteadyHandCharges = stellarSteadyHandCharges,
                StellarSteadyHand = root.StellarSteadyHandLeft,
                craft.SplendorCosmic,
                Expedience = root.ExpedienceLeft > 0,
                Condition = (int)root.Condition,
                CrafterDelineations = root.CrafterDelineationsLeft,
            },
        };
        return System.Text.Json.JsonSerializer.Serialize(request, RequestSerializerOptions);
    }

    internal static bool ResolveProgressFirst(
        CraftState craft,
        SolveMode solveMode,
        bool experimentalProgressPriorityEnabled)
        => experimentalProgressPriorityEnabled
            && solveMode == SolveMode.LiveAdaptive
            && !craft.CraftExpert;

    private static int Combo(StepState root) => root.ComboAction switch
    {
        VulcanSkill.BasicTouch => 1,
        VulcanSkill.StandardTouch or VulcanSkill.Observe => 2,
        _ when root.Index == 1 => 3,
        _ => 0,
    };
}
