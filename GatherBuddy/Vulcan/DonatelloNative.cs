using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Newtonsoft.Json;

namespace GatherBuddy.Vulcan;

internal static partial class DonatelloNative
{
    private const string LibraryName = "donatello_ffi.dll";
    internal const uint AbiVersion = 5;
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
        public string? Error { get; set; }
    }

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
            GatherBuddy.Config.RaphaelSolverConfig.RaphaelAllowSpecialistActions,
            GatherBuddy.Config.RaphaelSolverConfig.RaphaelBackloadProgress,
            interrupt,
            incumbent);

    internal static unsafe IReadOnlyList<VulcanSkill> Solve(
        CraftState craft,
        StepState root,
        bool allowSpecialistActions,
        bool backloadProgress,
        IntPtr interrupt = default,
        IReadOnlyList<VulcanSkill>? incumbent = null)
    {
        if (donatello_abi_version() != AbiVersion)
            throw new InvalidOperationException("Unsupported Donatello native ABI version");

        ConfigureCache(GatherBuddy.Config.RaphaelSolverConfig.DonatelloCacheMemoryMiB);
        var fallbackMinimizeSteps = GatherBuddy.Config.RaphaelSolverConfig.DonatelloMinimizeSteps;

        var bytes = Encoding.UTF8.GetBytes(SerializeRequest(
            craft, root, allowSpecialistActions, backloadProgress, incumbent, fallbackMinimizeSteps));
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
            return response.ActionIds.ConvertAll(id => (VulcanSkill)id);
        }
        finally
        {
            donatello_string_free(nativeResponse);
        }
    }

    internal static string SerializeRequest(
        CraftState craft,
        StepState root,
        bool allowSpecialistActions,
        bool backloadProgress,
        IReadOnlyList<VulcanSkill>? incumbent = null,
        bool fallbackMinimizeSteps = false)
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
            MaxQuality = craft.DonatelloOptions?.Objective == DonatelloSolveObjective.ProgressOnly
                ? 0
                : craft.CraftQualityMax,
            BaseProgress = Simulator.BaseProgress(craft),
            BaseQuality = Simulator.BaseQuality(craft),
            JobLevel = craft.StatLevel,
            Manipulation = craft.UnlockedManipulation,
            Specialist = craft.Specialist && allowSpecialistActions,
            BackloadProgress = backloadProgress,
            Objective = craft.DonatelloOptions?.Objective ?? DonatelloSolveObjective.MaximizeQuality,
            MinimizeSteps = craft.DonatelloOptions?.MinimizeSteps ?? fallbackMinimizeSteps,
            StellarSteadyHandCharges = stellarSteadyHandCharges,
            IncumbentActionIds = incumbent?.Select(action => (uint)action).ToArray() ?? [],
            Root = new
            {
                Cp = root.RemainingCP,
                root.Durability,
                root.Progress,
                Quality = craft.DonatelloOptions?.Objective == DonatelloSolveObjective.ProgressOnly
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
                QuickInnovationAvailable = root.QuickInnoAvailable,
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

    private static int Combo(StepState root) => root.PrevComboAction switch
    {
        VulcanSkill.BasicTouch => 1,
        VulcanSkill.StandardTouch or VulcanSkill.Observe => 2,
        _ when root.Index == 1 => 3,
        _ => 0,
    };
}
