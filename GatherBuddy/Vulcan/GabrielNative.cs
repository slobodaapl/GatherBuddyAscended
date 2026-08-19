using System;
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
    private const int GabrielProductivePotencyUnits = 3;
    private const int GabrielSupportActionAllowance = 10;
    private const int GabrielZeroStepDecisionAllowance = 9;
    private const int GabrielHorizonLimit = byte.MaxValue;
    internal const int DefaultGabrielWorkerThreads = 4;
    private const int MaximumGabrielWorkerThreads = 256;
    private static readonly SemaphoreSlim GabrielEstimateGate = new(1, 1);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe IntPtr gabriel_solve_json(byte* data, nuint length);

    private sealed class GabrielNativeResponse
    {
        public bool Ok { get; set; }
        public uint? ActionId { get; set; }
        public bool Planned { get; set; }
        public bool FailureClosure { get; set; }
        public int CandidateCount { get; set; }
        public int RolloutCount { get; set; }
        public int Successes { get; set; }
        public int Samples { get; set; }
        public double Probability { get; set; }
        public long ElapsedMillis { get; set; }
        public string? Error { get; set; }
    }

    internal sealed record GabrielRecommendation(
        VulcanSkill Action,
        bool Planned,
        bool FailureClosure,
        int CandidateCount,
        int RolloutCount,
        long ElapsedMillis);

    internal sealed record GabrielProbabilityEstimate(
        int Successes,
        int Samples,
        double Probability,
        long ElapsedMillis);

    internal static GabrielRecommendation RecommendGabriel(
        CraftState craft,
        StepState root,
        int decisions,
        ulong seed)
    {
        var response = InvokeGabriel(craft, root, decisions, seed, operation: 0, samples: 0);
        if (!response.ActionId.HasValue || !IsValidGabrielRecommendation((VulcanSkill)response.ActionId.Value))
            throw new InvalidOperationException("Gabriel native response omitted a valid non-forbidden action");
        return new(
            (VulcanSkill)response.ActionId.Value,
            response.Planned,
            response.FailureClosure,
            response.CandidateCount,
            response.RolloutCount,
            response.ElapsedMillis);
    }

    internal static GabrielProbabilityEstimate EstimateGabriel(
        CraftState craft,
        StepState root,
        int decisions,
        ulong seed,
        int samples)
    {
        var response = InvokeGabriel(craft, root, decisions, seed, operation: 1, samples);
        if (response.Samples != samples || response.Successes < 0 || response.Successes > response.Samples)
            throw new InvalidOperationException("Gabriel native response contained an invalid probability estimate");
        return new(response.Successes, response.Samples, response.Probability, response.ElapsedMillis);
    }

    private static unsafe GabrielNativeResponse InvokeGabriel(
        CraftState craft,
        StepState root,
        int decisions,
        ulong seed,
        int operation,
        int samples)
    {
        if (donatello_abi_version() != AbiVersion)
            throw new InvalidOperationException("Unsupported Donatello native ABI version");
        if (!GabrielPolicyCatalog.TryResolve(craft, out var policy, out var reason))
            throw new InvalidOperationException(reason);
        if (!ExpertConditionProfileCatalog.TryGet(craft.RecipeLevelTableId, out var conditionProfile))
            throw new InvalidOperationException("Gabriel condition profile disappeared after eligibility validation");

        var gate = operation == 1 ? GabrielEstimateGate : null;
        gate?.Wait();
        try
        {
            var bytes = Encoding.UTF8.GetBytes(SerializeGabrielRequest(
                craft,
                root,
                decisions,
                seed,
                operation,
                samples,
                policy,
                conditionProfile));
            IntPtr nativeResponse;
            fixed (byte* data = bytes)
                nativeResponse = gabriel_solve_json(data, (nuint)bytes.Length);
            if (nativeResponse == IntPtr.Zero)
                throw new InvalidOperationException("Gabriel native solver returned null");
            try
            {
                var json = Marshal.PtrToStringUTF8(nativeResponse)
                    ?? throw new InvalidOperationException("Gabriel native response was not UTF-8");
                var response = JsonConvert.DeserializeObject<GabrielNativeResponse>(json)
                    ?? throw new InvalidOperationException("Gabriel native response was empty");
                if (!response.Ok)
                    throw new InvalidOperationException(response.Error ?? "Gabriel solve failed");
                return response;
            }
            finally
            {
                donatello_string_free(nativeResponse);
            }
        }
        finally
        {
            gate?.Release();
        }
    }

    internal static string SerializeGabrielRequest(
        CraftState craft,
        StepState root,
        int decisions,
        ulong seed,
        int operation,
        int samples,
        GabrielPolicyDescriptor policy,
        ExpertConditionProfile conditionProfile)
    {
        var (maxSteps, maxDecisions, currentStep, currentDecisions) = CalculateHorizon(craft, root, decisions);
        var request = new
        {
            AbiVersion,
            Operation = operation,
            craft.RecipeLevelTableId,
            PolicyProfile = (int)policy.Profile,
            MaxCp = craft.StatCP,
            MaxDurability = craft.CraftDurability,
            MaxProgress = craft.CraftProgress,
            MaxQuality = craft.CraftQualityMax,
            RequiredQuality = craft.CraftQualityMax,
            BaseProgress = Simulator.BaseProgress(craft),
            BaseQuality = Simulator.BaseQuality(craft),
            JobLevel = craft.StatLevel,
            Manipulation = craft.UnlockedManipulation,
            Specialist = craft.Specialist && CraftingContextResolver.ResolveSpecialistActionsAllowed(craft),
            ConditionProbabilitiesBps = conditionProfile.BaseProbabilityBasisPoints.ToArray(),
            WorkerThreads = ResolveGabrielWorkerThreads(craft.GabrielWorkerThreads),
            MaxSteps = maxSteps,
            MaxDecisions = maxDecisions,
            Samples = Math.Max(0, samples),
            Seed = seed,
            Root = new
            {
                Cp = root.RemainingCP,
                root.Durability,
                root.Progress,
                root.Quality,
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
                StellarSteadyHandCharges = 0,
                StellarSteadyHand = 0,
                craft.SplendorCosmic,
                Expedience = root.ExpedienceLeft > 0,
                Condition = (int)root.Condition,
                CrafterDelineations = root.CrafterDelineationsLeft,
                Step = currentStep,
                Decisions = currentDecisions,
            },
        };
        return System.Text.Json.JsonSerializer.Serialize(request, RequestSerializerOptions);
    }

    internal static int ResolveGabrielWorkerThreads(int requested)
        => Math.Clamp(
            requested,
            1,
            Math.Min(Math.Max(1, Environment.ProcessorCount), MaximumGabrielWorkerThreads));

    internal static bool IsValidGabrielRecommendation(VulcanSkill action)
        => action.IsExecutableAction()
            && action is not VulcanSkill.FinalAppraisal
            && action is not VulcanSkill.CarefulObservation
            && action is not VulcanSkill.QuickInnovation
            && action is not VulcanSkill.StellarSteadyHand;

    private static (int MaxSteps, int MaxDecisions, int CurrentStep, int CurrentDecisions) CalculateHorizon(
        CraftState craft,
        StepState root,
        int decisions)
    {
        var currentStep = Math.Clamp(root.Index - 1, 0, GabrielHorizonLimit - 1);
        var currentDecisions = Math.Clamp(decisions, 0, GabrielHorizonLimit - 1);
        var progressUnit = Math.Max(1, Simulator.BaseProgress(craft) * GabrielProductivePotencyUnits);
        var qualityUnit = Math.Max(1, Simulator.BaseQuality(craft) * GabrielProductivePotencyUnits);
        var recipeProductiveSteps = DivideCeiling(craft.CraftProgress, progressUnit)
            + DivideCeiling(craft.CraftQualityMax, qualityUnit);
        var recipeSteps = Math.Max(1, recipeProductiveSteps + GabrielSupportActionAllowance);
        var remainingProgress = Math.Max(0, craft.CraftProgress - root.Progress);
        var remainingQuality = Math.Max(0, craft.CraftQualityMax - root.Quality);
        var remainingProductiveSteps = DivideCeiling(remainingProgress, progressUnit)
            + DivideCeiling(remainingQuality, qualityUnit);
        var remainingSteps = Math.Max(1, remainingProductiveSteps + GabrielSupportActionAllowance);
        var maxSteps = Math.Clamp(
            currentStep < recipeSteps ? recipeSteps : currentStep + remainingSteps,
            currentStep + 1,
            GabrielHorizonLimit);
        var recipeDecisions = recipeSteps + GabrielZeroStepDecisionAllowance;
        var maxDecisions = Math.Max(
            maxSteps,
            Math.Clamp(
                currentDecisions < recipeDecisions
                    ? recipeDecisions
                    : currentDecisions + remainingSteps + GabrielZeroStepDecisionAllowance,
                currentDecisions + 1,
                GabrielHorizonLimit));
        return (maxSteps, maxDecisions, currentStep, currentDecisions);
    }

    private static int DivideCeiling(int value, int divisor)
        => (value + divisor - 1) / divisor;
}
