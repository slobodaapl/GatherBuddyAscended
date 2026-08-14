using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Crafting;

internal static unsafe class CraftingJobLevelReader
{
    internal static bool TryRead(uint jobId, out int level)
    {
        level = 0;
        if (jobId == 0)
            return false;

        var playerState = PlayerState.Instance();
        var classJobs = Dalamud.GameData.GetExcelSheet<ClassJob>();
        if (playerState == null
         || classJobs?.TryGetRow(jobId, out var classJob) != true
         || classJob.ExpArrayIndex < 0)
            return false;

        level = playerState->ClassJobLevels[classJob.ExpArrayIndex];
        return true;
    }

    internal static int ReadOrDefault(uint jobId)
        => TryRead(jobId, out var level) ? level : 0;
}
