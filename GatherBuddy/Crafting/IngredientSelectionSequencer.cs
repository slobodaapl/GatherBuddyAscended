namespace GatherBuddy.Crafting;

internal enum EquipmentIngredientSelectionPhase
{
    None,
    WaitingForMenu,
    WaitingForAssignment,
}

internal sealed class IngredientSelectionSequencer
{
    public EquipmentIngredientSelectionPhase Phase { get; private set; }
    public uint ItemId { get; private set; }
    public bool HighQuality { get; private set; }
    public long ResumeAt { get; private set; }

    public bool IsReady(long now)
        => now >= ResumeAt;

    public void BeginEquipment(uint itemId, bool highQuality, long now)
    {
        Phase = EquipmentIngredientSelectionPhase.WaitingForMenu;
        ItemId = itemId;
        HighQuality = highQuality;
        ResumeAt = now + 150;
    }

    public void MarkMenuSelectionComplete(long now)
    {
        Phase = EquipmentIngredientSelectionPhase.WaitingForAssignment;
        ResumeAt = now + 200;
    }

    public void DelayNormalAssignment(long now)
    {
        Reset();
        ResumeAt = now + 50;
    }

    public void CompleteEquipmentAssignment()
        => Reset();

    public void Reset()
    {
        Phase = EquipmentIngredientSelectionPhase.None;
        ItemId = 0;
        HighQuality = false;
        ResumeAt = 0;
    }
}
