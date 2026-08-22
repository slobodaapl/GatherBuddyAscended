using GatherBuddy.AutoGather;
using GatherBuddy.Time;
using GatherBuddy.Vulcan;
using System;

namespace GatherBuddy.Vulcan.Tests;

public static class CollectableGpPlanningAcceptanceTests
{
    public static void Run(Action<bool, string> require)
    {
        var request = Request(GatheringSolverMode.ExpectedScrip, gp: 0, planStartingGp: true);
        var decision = DonatelloNative.SolveGathering(request);

        require(decision.SolverUsed == GatheringSolverMode.ExpectedScrip
             && decision.Action == GatheringSolverAction.Scour,
            "the live native boundary must preserve the best action for the observed low-GP state");
        require(decision.MinimumStartingGp == 200,
            "the native GP frontier must return the lowest GP matching the full-GP expected-scrip result");
        require(decision.GpPlanningError == null,
            "a valid starting-GP frontier must not report a planning failure");

        var sufficient = DonatelloNative.SolveGathering(
            Request(GatheringSolverMode.ExpectedScrip, gp: 200, planStartingGp: false));
        require(sufficient.Action == GatheringSolverAction.Scrutiny
             && sufficient.MinimumStartingGp == null,
            "a post-wait live re-solve must use the observed GP without repeating frontier analysis");

        var reduction = DonatelloNative.SolveGathering(
            Request(GatheringSolverMode.MaximizeCollectability, gp: 0, planStartingGp: true));
        require(reduction.MinimumStartingGp == 200
             && reduction.ExpectedPerfectCollects == 0,
            "reduction planning must derive its GP threshold from the perfect-collect objective instead of forcing maximum GP");

        var now = new TimeStamp(2_000_000);
        require(CollectableGpWaitPolicy.ResolveRequiredGp(0, 200, 1000) == 200
             && CollectableGpWaitPolicy.ResolveRequiredGp(400, 200, 1000) == 400,
            "the solver threshold must replace the maximum-GP sentinel while preserving a higher configured floor");
        require(CollectableGpWaitPolicy.ShouldWait(199, 200, TimeInterval.Always, now)
             && !CollectableGpWaitPolicy.ShouldWait(200, 200, TimeInterval.Always, now),
            "runtime admission must wait only below the derived threshold");
        require(!CollectableGpWaitPolicy.ShouldWait(
                0,
                200,
                new TimeInterval(now.AddMinutes(-1), now.AddSeconds(60)),
                now),
            "the timed-node reserve must override solver-requested waiting");
    }

    private static GatheringSolveRequest Request(
        GatheringSolverMode mode,
        int gp,
        bool planStartingGp)
        => new(
            mode,
            new GatheringSolverState(
                Collectability: 0,
                Integrity: 2,
                MaxIntegrity: 2,
                Gp: gp,
                MaxGp: 1000,
                Remaining: 1,
                Scrutiny: false,
                CollectorsFocus: false,
                PrimingTouch: false,
                Standard: 0,
                Eureka: false,
                RevisitUsed: false),
            [
                new GatheringRewardTier(500, 10),
                new GatheringRewardTier(1000, 100),
            ],
            new GatheringActionModel(
                ScourGain: 500,
                MeticulousGain: 500,
                BrazenGains: [new GatheringWeightedGain(500, 1)],
                ScrutinyCost: 200,
                FocusCost: 100,
                PrimingCost: 100,
                SolidReasonCost: 300,
                Scour: true,
                Brazen: false,
                Meticulous: false,
                Scrutiny: true,
                CollectorsFocus: false,
                PrimingTouch: false,
                SolidReason: false,
                WiseToTheWorld: false),
            new GatheringMechanics(
                GatherSuccessBp: 10000,
                IntuitionBp: 0,
                FocusIntuitionBp: 0,
                IntuitionGain: 0,
                StandardProcBp: 0,
                HighStandardUpgradeBp: 0,
                MeticulousPreserveBp: 0,
                HighStandardPreserveBonusBp: 0,
                PrimingPreserveMultiplier: 1,
                SolidReasonEurekaBp: 0,
                RevisitBp: 0,
                CollectGpRegen: 6,
                ScrutinyGainMultiplierBp: 20000,
                MaxStates: 10000),
            new GatheringLegacyOptions(
                TargetScore: 1000,
                MinimumScore: 500,
                UseFullRotation: true,
                AlwaysUseSolidReason: false,
                AbandonWhenComplete: false),
            UnsupportedReason: null,
            PlanStartingGp: planStartingGp);
}
