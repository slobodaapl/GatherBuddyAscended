using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GatherBuddy.Vulcan;

public enum CollectableSolverMode
{
    ExpectedScrip = 0,
    Legacy = 1,
}

internal enum GatheringSolverAction
{
    Scour,
    Brazen,
    Meticulous,
    Scrutiny,
    CollectorsFocus,
    PrimingTouch,
    SolidReason,
    WiseToTheWorld,
    Collect,
}

internal sealed record GatheringSolverState(
    int Collectability,
    int Integrity,
    int MaxIntegrity,
    int Gp,
    int MaxGp,
    int Remaining,
    bool Scrutiny,
    bool CollectorsFocus,
    bool PrimingTouch,
    int Standard,
    bool Eureka,
    bool RevisitUsed);

internal sealed record GatheringRewardTier(int Threshold, int Scrip);
internal sealed record GatheringWeightedGain(int Gain, int Weight);

internal sealed record GatheringActionModel(
    int ScourGain,
    int MeticulousGain,
    IReadOnlyList<GatheringWeightedGain> BrazenGains,
    int ScrutinyCost,
    int FocusCost,
    int PrimingCost,
    int SolidReasonCost,
    bool Scour,
    bool Brazen,
    bool Meticulous,
    bool Scrutiny,
    bool CollectorsFocus,
    bool PrimingTouch,
    bool SolidReason,
    bool WiseToTheWorld);

internal sealed record GatheringMechanics(
    int GatherSuccessBp,
    int IntuitionBp,
    int FocusIntuitionBp,
    int IntuitionGain,
    int StandardProcBp,
    int HighStandardUpgradeBp,
    int MeticulousPreserveBp,
    int HighStandardPreserveBonusBp,
    int PrimingPreserveMultiplier,
    int SolidReasonEurekaBp,
    int RevisitBp,
    int CollectGpRegen,
    int ScrutinyGainMultiplierBp,
    int MaxStates);

internal sealed record GatheringLegacyOptions(
    int TargetScore,
    int MinimumScore,
    bool UseFullRotation,
    bool AlwaysUseSolidReason,
    bool AbandonWhenComplete);

internal sealed record GatheringSolveRequest(
    CollectableSolverMode Mode,
    GatheringSolverState State,
    IReadOnlyList<GatheringRewardTier> Rewards,
    GatheringActionModel Actions,
    GatheringMechanics Mechanics,
    GatheringLegacyOptions Legacy,
    string? UnsupportedReason);

internal sealed record GatheringDecision(
    GatheringSolverAction Action,
    CollectableSolverMode SolverUsed,
    double ExpectedScrip,
    double ExpectedTerminalGp,
    string? FallbackReason);

internal static partial class DonatelloNative
{
    private static readonly JsonSerializerOptions GatheringSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe IntPtr donatello_gathering_solve_json(byte* data, nuint length);

    private sealed class GatheringNativeResponse
    {
        public bool Ok { get; set; }
        public GatheringDecision? Decision { get; set; }
        public string? Error { get; set; }
    }

    internal static unsafe GatheringDecision SolveGathering(GatheringSolveRequest request)
    {
        if (donatello_abi_version() != AbiVersion)
            throw new InvalidOperationException("Unsupported Donatello native ABI version");

        var bytes = Encoding.UTF8.GetBytes(SerializeGatheringRequest(request));
        IntPtr nativeResponse;
        fixed (byte* data = bytes)
            nativeResponse = donatello_gathering_solve_json(data, (nuint)bytes.Length);
        if (nativeResponse == IntPtr.Zero)
            throw new InvalidOperationException("Donatello native gathering solver returned null");
        try
        {
            var json = Marshal.PtrToStringUTF8(nativeResponse)
                ?? throw new InvalidOperationException("Donatello native gathering response was not UTF-8");
            var response = JsonSerializer.Deserialize<GatheringNativeResponse>(json, GatheringSerializerOptions)
                ?? throw new InvalidOperationException("Donatello native gathering response was empty");
            if (!response.Ok || response.Decision == null)
                throw new InvalidOperationException(response.Error ?? "Donatello gathering solve failed");
            return response.Decision;
        }
        finally
        {
            donatello_string_free(nativeResponse);
        }
    }

    internal static string SerializeGatheringRequest(GatheringSolveRequest request)
        => JsonSerializer.Serialize(request, GatheringSerializerOptions);
}
