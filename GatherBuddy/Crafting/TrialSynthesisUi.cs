using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GatherBuddy.Automation;

namespace GatherBuddy.Crafting;

internal static unsafe class TrialSynthesisUi
{
    private const int TrialSynthesisCallbackId = 10;

    public static bool TryRequestStart(uint expectedRecipeId)
    {
        var address = Dalamud.GameGui.GetAddonByName("RecipeNote").Address;
        var addon = (AddonRecipeNote*)address;
        if (addon == null || !addon->AtkUnitBase.IsVisible || !addon->AtkUnitBase.IsReady)
            return false;

        var selectedRecipe = RecipeNoteExt.GetSelectedRecipeEntry();
        if (selectedRecipe == null || selectedRecipe->RecipeId != expectedRecipeId)
            return false;

        var confirmationAddress = Dalamud.GameGui.GetAddonByName("SelectYesno").Address;
        var confirmation = (AddonSelectYesno*)confirmationAddress;
        if (confirmation != null && confirmation->AtkUnitBase.IsVisible && confirmation->AtkUnitBase.IsReady)
            return false;
        var settingsAddress = Dalamud.GameGui.GetAddonByName("RecipeNotePraticeSetting").Address;
        var settings = (AtkUnitBase*)settingsAddress;
        if (settings != null && settings->IsVisible && settings->IsReady)
            return false;

        var button = addon->TrialSynthesisButton;
        if (button == null || !button->IsEnabled)
            return false;
        var ownerNode = button->AtkComponentBase.OwnerNode;
        if (ownerNode == null || !ownerNode->AtkResNode.IsVisible())
            return false;

        Callback.Fire((AtkUnitBase*)addon, true, TrialSynthesisCallbackId);
        return true;
    }

    public static bool TryConfirmStart(uint expectedRecipeId)
    {
        var settingsAddress = Dalamud.GameGui.GetAddonByName("RecipeNotePraticeSetting").Address;
        var settings = (AtkUnitBase*)settingsAddress;
        if (settings != null && settings->IsVisible && settings->IsReady)
        {
            Callback.Fire(settings, true, 0, 0, false);
            return true;
        }

        var selectedRecipe = RecipeNoteExt.GetSelectedRecipeEntry();
        if (selectedRecipe == null || selectedRecipe->RecipeId != expectedRecipeId)
            return false;

        var confirmationAddress = Dalamud.GameGui.GetAddonByName("SelectYesno").Address;
        var confirmation = (AddonSelectYesno*)confirmationAddress;
        if (confirmation == null || !confirmation->AtkUnitBase.IsVisible || !confirmation->AtkUnitBase.IsReady)
            return false;

        new AddonMaster.SelectYesno(confirmation).Yes();
        return true;
    }

    public static bool IsActive(uint expectedRecipeId)
    {
        var eventFramework = EventFramework.Instance();
        var handler = eventFramework == null ? null : eventFramework->GetCraftEventHandler();
        return handler != null
            && handler->RecipeId == expectedRecipeId
            && (handler->CraftFlags & CraftFlags.NotTrialSynthesis) == 0;
    }
}
