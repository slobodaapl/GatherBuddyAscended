using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Newtonsoft.Json;

namespace GatherBuddy.Vulcan;

internal static class DonatelloNative
{
    private const string LibraryName = "donatello_ffi.dll";
    private const uint AbiVersion = 2;
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

    private sealed class NativeResponse
    {
        public bool Ok { get; set; }
        public List<uint>? ActionIds { get; set; }
        public string? Error { get; set; }
    }

    public static IntPtr CreateInterrupt()
        => donatello_interrupt_create();

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
        IntPtr interrupt = default)
        => Solve(
            craft,
            root,
            GatherBuddy.Config.RaphaelSolverConfig.RaphaelAllowSpecialistActions,
            GatherBuddy.Config.RaphaelSolverConfig.RaphaelBackloadProgress,
            interrupt);

    internal static unsafe IReadOnlyList<VulcanSkill> Solve(
        CraftState craft,
        StepState root,
        bool allowSpecialistActions,
        bool backloadProgress,
        IntPtr interrupt = default)
    {
        if (donatello_abi_version() != AbiVersion)
            throw new InvalidOperationException("Unsupported Donatello native ABI version");

        var bytes = Encoding.UTF8.GetBytes(SerializeRequest(
            craft, root, allowSpecialistActions, backloadProgress));
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
        bool backloadProgress)
    {
        var request = new
        {
            AbiVersion,
            MaxCp = craft.StatCP,
            MaxDurability = craft.CraftDurability,
            MaxProgress = craft.CraftProgress,
            MaxQuality = craft.CraftQualityMax,
            BaseProgress = Simulator.BaseProgress(craft),
            BaseQuality = Simulator.BaseQuality(craft),
            JobLevel = craft.StatLevel,
            Manipulation = craft.UnlockedManipulation,
            Specialist = craft.Specialist && allowSpecialistActions,
            BackloadProgress = backloadProgress,
            Root = new
            {
                Cp = root.RemainingCP,
                root.Durability,
                root.Progress,
                root.Quality,
                InnerQuiet = root.IQStacks,
                WasteNot = root.WasteNotLeft,
                root.ManipulationLeft,
                root.InnovationLeft,
                root.VenerationLeft,
                root.GreatStridesLeft,
                root.MuscleMemoryLeft,
                FinalAppraisal = root.FinalAppraisalLeft,
                CarefulObservationCharges = root.CarefulObservationLeft,
                Combo = Combo(root),
                root.HeartAndSoulActive,
                root.HeartAndSoulAvailable,
                QuickInnovationAvailable = root.QuickInnoAvailable,
                root.TrainedPerfectionActive,
                root.TrainedPerfectionAvailable,
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
