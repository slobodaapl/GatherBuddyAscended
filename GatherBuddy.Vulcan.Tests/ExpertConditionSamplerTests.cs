using GatherBuddy.Crafting;

namespace GatherBuddy.Vulcan.Tests;

internal static class ExpertConditionSamplerTests
{
    public static void Run(Action<bool, string> require)
    {
        const ushort supportedExpertFlags = 2035;
        foreach (var conditionId in new byte[] { 1, 2, 5, 6, 7, 8, 9, 10, 11 })
            require(ExpertConditionSampler.IsAllowedByConditionsFlag(supportedExpertFlags, conditionId),
                $"supported expert condition flags must admit raw condition {conditionId}");
        require(!ExpertConditionSampler.IsAllowedByConditionsFlag(supportedExpertFlags, 3)
                && !ExpertConditionSampler.IsAllowedByConditionsFlag(supportedExpertFlags, 4),
            "supported expert condition flags must reject Excellent and Poor");

        const ushort aqueductFlags = 1523;
        foreach (var conditionId in new byte[] { 1, 2, 5, 6, 7, 8, 9, 11 })
            require(ExpertConditionSampler.IsAllowedByConditionsFlag(aqueductFlags, conditionId),
                $"Aqueduct condition flags must admit raw condition {conditionId}");
        require(!ExpertConditionSampler.IsAllowedByConditionsFlag(aqueductFlags, 3)
                && !ExpertConditionSampler.IsAllowedByConditionsFlag(aqueductFlags, 4)
                && !ExpertConditionSampler.IsAllowedByConditionsFlag(aqueductFlags, 10)
                && !ExpertConditionSampler.IsAllowedByConditionsFlag(aqueductFlags, 12),
            "Aqueduct condition flags must reject Excellent, Poor, Good Omen, and unknown raw conditions");

        var matrix = new ExpertConditionTransitionMatrix();
        matrix.Add(10, 2); // Good Omen -> Good: guaranteed sequence edge.
        matrix.Add(10, 2);
        matrix.Add(1, 2);  // Normal -> Good: independently observed edge.
        matrix.Add(11, 6); // Robust -> Sturdy: separate sequence edge.
        matrix.Add(5, 6);  // Centered -> Sturdy: independently observed edge.
        matrix.Add(2, 12); // Unknown destination must remain captured.

        require(matrix.Total == 6
                && matrix.GetCount(10, 2) == 2
                && matrix.GetCount(1, 2) == 1,
            "Good Omen -> Good must remain separate from independently observed Good outcomes");
        require(matrix.GetCount(11, 6) == 1
                && matrix.GetCount(5, 6) == 1,
            "Robust -> Sturdy must remain separate from independently observed Sturdy outcomes");
        require(matrix.GetCount(2, 12) == 1,
            "unknown condition transitions must be retained instead of discarded");

        var budget = new ExpertConditionSamplingRunBudget(100);
        var firstNinetyNineRestart = true;
        for (var run = 1; run < 100; ++run)
            firstNinetyNineRestart &= budget.RecordCompletedRun();
        require(firstNinetyNineRestart && budget.Completed == 99,
            "the first 99 completed Trial sessions must request another run");
        require(!budget.RecordCompletedRun() && budget.Completed == 100,
            "the 100th completed Trial session must stop automatic restart exactly at the target");
        require(!budget.RecordCompletedRun() && budget.Completed == 100,
            "a reached Trial-session target must not over-count or restart again");
    }
}
