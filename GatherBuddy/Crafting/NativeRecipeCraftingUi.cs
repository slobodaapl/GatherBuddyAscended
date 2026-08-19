using System;
using System.Runtime.InteropServices;
using Dalamud.Game.Addon.Events;
using Dalamud.Game.Addon.Events.EventDataTypes;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GatherBuddy.Plugin;
using InteropGenerator.Runtime;

namespace GatherBuddy.Crafting;

internal sealed unsafe class NativeRecipeCraftingUi : IDisposable
{
    private delegate void NativeAddonRefresh(AtkUnitBase* addon);

    private const string RecipeNoteAddonName = "RecipeNote";
    private const string CosmicRecipeAddonName = "WKSRecipeNotebook";
    private const uint RecipeNoteSynthesizeNodeId = 104;
    private const uint CosmicSynthesizeNodeId = 50;
    private const uint CosmicCraftableCountNodeId = 34;
    private const uint ButtonNodeIdOffset = 100_000_000;
    private const uint InputNodeIdOffset = 110_000_000;

    private InjectedAddonState? _recipeNoteState;
    private InjectedAddonState? _cosmicState;
    private bool _disposed;

    private sealed class InjectedAddonState
    {
        public required string AddonName { get; init; }
        public nint AddonAddress { get; init; }
        public AtkComponentNode* ButtonNode { get; init; }
        public AtkComponentNode* InputNode { get; init; }
        public AtkComponentTextInput* InputComponent { get; init; }
        public delegate* unmanaged<AtkUnitBase*, InputCallbackType, CStringPointer, CStringPointer, int, InputCallbackResult> OriginalInputCallback { get; init; }
        public IAddonEventHandle? ButtonEvent { get; set; }
        public IAddonEventHandle? InputFocusEvent { get; set; }
        public NativeCraftAmountState Amount { get; } = new();
        public string LastInputText { get; set; } = string.Empty;
    }

    public NativeRecipeCraftingUi()
    {
        RegisterLifecycle(RecipeNoteAddonName, HandleRecipeNoteLifecycle);
        RegisterLifecycle(CosmicRecipeAddonName, HandleCosmicLifecycle);
        TryRefreshExistingAddon(RecipeNoteAddonName, RefreshRecipeNote);
        TryRefreshExistingAddon(CosmicRecipeAddonName, RefreshCosmic);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        UnregisterLifecycle(RecipeNoteAddonName, HandleRecipeNoteLifecycle);
        UnregisterLifecycle(CosmicRecipeAddonName, HandleCosmicLifecycle);
        ReleaseState(ref _recipeNoteState, addonFinalizing: false);
        ReleaseState(ref _cosmicState, addonFinalizing: false);
    }

    private static void RegisterLifecycle(string addonName, IAddonLifecycle.AddonEventDelegate handler)
    {
        Dalamud.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, addonName, handler);
        Dalamud.AddonLifecycle.RegisterListener(AddonEvent.PostRefresh, addonName, handler);
        Dalamud.AddonLifecycle.RegisterListener(AddonEvent.PostRequestedUpdate, addonName, handler);
        Dalamud.AddonLifecycle.RegisterListener(AddonEvent.PostUpdate, addonName, handler);
        Dalamud.AddonLifecycle.RegisterListener(AddonEvent.PreFinalize, addonName, handler);
    }

    private static void UnregisterLifecycle(string addonName, IAddonLifecycle.AddonEventDelegate handler)
    {
        Dalamud.AddonLifecycle.UnregisterListener(AddonEvent.PostSetup, addonName, handler);
        Dalamud.AddonLifecycle.UnregisterListener(AddonEvent.PostRefresh, addonName, handler);
        Dalamud.AddonLifecycle.UnregisterListener(AddonEvent.PostRequestedUpdate, addonName, handler);
        Dalamud.AddonLifecycle.UnregisterListener(AddonEvent.PostUpdate, addonName, handler);
        Dalamud.AddonLifecycle.UnregisterListener(AddonEvent.PreFinalize, addonName, handler);
    }

    private static void TryRefreshExistingAddon(string addonName, NativeAddonRefresh refresh)
    {
        if (Dalamud.GameGui.GetAddonByName(addonName) is { Address: not 0 } addon)
            refresh((AtkUnitBase*)addon.Address);
    }

    private void HandleRecipeNoteLifecycle(AddonEvent type, AddonArgs args)
    {
        var addon = (AtkUnitBase*)args.Addon.Address;
        if (type == AddonEvent.PreFinalize)
        {
            ReleaseState(ref _recipeNoteState, addonFinalizing: true);
            return;
        }

        RefreshRecipeNote(addon);
    }

    private void HandleCosmicLifecycle(AddonEvent type, AddonArgs args)
    {
        var addon = (AtkUnitBase*)args.Addon.Address;
        if (type == AddonEvent.PreFinalize)
        {
            ReleaseState(ref _cosmicState, addonFinalizing: true);
            return;
        }

        RefreshCosmic(addon);
    }

    private void RefreshRecipeNote(AtkUnitBase* addon)
    {
        if (_disposed || addon == null || addon->RootNode == null)
            return;
        if (IPCSubscriber.IsReady("Artisan"))
        {
            SetStateVisibility(_recipeNoteState, visible: false, enabled: false);
            return;
        }

        var typedAddon = (AddonRecipeNote*)addon;
        var synthResNode = addon->GetNodeById(RecipeNoteSynthesizeNodeId);
        var synthNode = synthResNode == null ? null : synthResNode->GetAsAtkComponentNode();
        var searchInput = typedAddon->SearchTextInput;
        if (synthNode == null || synthNode->Component == null || searchInput == null || searchInput->OwnerNode == null)
        {
            SetStateVisibility(_recipeNoteState, visible: false, enabled: false);
            return;
        }

        if (!EnsureRecipeNoteState(addon, synthNode, searchInput))
            return;
        var state = _recipeNoteState!;

        if (!TryGetSelectedRecipe(out var recipeId, out var requiredJob)
            || !CraftingGameInterop.ManualTakeoverMatchesRecipeClass(requiredJob, Dalamud.Objects.LocalPlayer?.ClassJob.RowId ?? 0)
            || !TryReadRecipeNoteCraftableCount(typedAddon, out var maximum))
        {
            SetStateVisibility(state, visible: false, enabled: false);
            return;
        }

        var recipeChanged = state.Amount.RecipeId != recipeId;
        var previousValue = state.Amount.Value;
        state.Amount.Refresh(recipeId, maximum);
        if (recipeChanged || state.Amount.Value != previousValue)
            SetInputText(state, state.Amount.Value.ToString());
        else
            PollInputText(state);

        var busy = CraftingGatherBridge.HasActiveQueue;
        SetStateVisibility(state, visible: true, enabled: !busy && maximum > 0);
        state.InputComponent->SetEnabledState(!busy && maximum > 0);
        state.ButtonNode->Component->SetEnabledState(!busy && state.Amount.Value > 0);
        ((AtkComponentButton*)state.ButtonNode->Component)->SetText($"Craft {state.Amount.Value}");
    }

    private void RefreshCosmic(AtkUnitBase* addon)
    {
        if (_disposed || addon == null || addon->RootNode == null)
            return;
        if (IPCSubscriber.IsReady("Artisan"))
        {
            SetStateVisibility(_cosmicState, visible: false, enabled: false);
            return;
        }

        var synthResNode = addon->GetNodeById(CosmicSynthesizeNodeId);
        var synthNode = synthResNode == null ? null : synthResNode->GetAsAtkComponentNode();
        if (synthNode == null || synthNode->Component == null)
        {
            SetStateVisibility(_cosmicState, visible: false, enabled: false);
            return;
        }

        if (!EnsureCosmicState(addon, synthNode))
            return;
        var state = _cosmicState!;

        if (!TryGetSelectedRecipe(out _, out var requiredJob)
            || !CraftingGameInterop.ManualTakeoverMatchesRecipeClass(requiredJob, Dalamud.Objects.LocalPlayer?.ClassJob.RowId ?? 0)
            || !TryReadDisplayedCount(addon->GetTextNodeById(CosmicCraftableCountNodeId), out var maximum))
        {
            SetStateVisibility(state, visible: false, enabled: false);
            return;
        }

        var enabled = !CraftingGatherBridge.HasActiveQueue && maximum > 0;
        SetStateVisibility(state, visible: true, enabled: enabled);
        ((AtkComponentButton*)state.ButtonNode->Component)->SetText("Craft all");
    }

    private bool EnsureRecipeNoteState(
        AtkUnitBase* addon,
        AtkComponentNode* synthNode,
        AtkComponentTextInput* searchInput)
    {
        if (_recipeNoteState?.AddonAddress == (nint)addon)
            return true;
        if (_recipeNoteState != null)
            ReleaseState(ref _recipeNoteState, addonFinalizing: false);

        try
        {
            var buttonNode = GetOrDuplicateComponentNode(addon, synthNode, ButtonNodeIdOffset);
            var inputNode = GetOrDuplicateComponentNode(addon, searchInput->OwnerNode, InputNodeIdOffset);
            if (buttonNode == null || inputNode == null)
                throw new InvalidOperationException("The game did not duplicate the native RecipeNote controls.");
            if (buttonNode->Component == null || buttonNode->Component->GetComponentType() != ComponentType.Button)
                throw new InvalidOperationException("The duplicated RecipeNote craft control is not a button.");

            if (inputNode->Component == null || inputNode->Component->GetComponentType() != ComponentType.TextInput)
                throw new InvalidOperationException("The duplicated RecipeNote amount control is not a text input.");
            var inputComponent = (AtkComponentTextInput*)inputNode->Component;

            var state = new InjectedAddonState
            {
                AddonName = RecipeNoteAddonName,
                AddonAddress = (nint)addon,
                ButtonNode = buttonNode,
                InputNode = inputNode,
                InputComponent = inputComponent,
                OriginalInputCallback = searchInput->Callback,
            };
            _recipeNoteState = state;

            PositionRecipeNoteControls(synthNode, buttonNode, inputNode);
            AttachAfterTarget((AtkResNode*)buttonNode, (AtkResNode*)synthNode);
            AttachAfterTarget((AtkResNode*)inputNode, (AtkResNode*)buttonNode);
            inputComponent->OwnerAddon = addon;
            inputComponent->ContainingAddon2 = addon;
            inputComponent->ToggleNumberInput(true);
            inputComponent->ToggleSymbolInput(false);
            inputComponent->ToggleIME(false);
            inputComponent->SetMaxByte(9);
            inputComponent->SetMaxChar(9);
            inputComponent->SetMaxLine(1);
            inputComponent->Callback = &SuppressRecipeSearchCallback;
            var buttonEventNode = PrepareButtonEventNode(buttonNode);
            state.ButtonEvent = Dalamud.AddonEventManager.AddEvent(
                (nint)addon,
                (nint)buttonEventNode,
                AddonEventType.MouseClick,
                HandleRecipeNoteButtonClick);
            if (state.ButtonEvent == null)
                throw new InvalidOperationException("The RecipeNote craft button event could not be registered.");
            var inputEventNode = inputComponent->CollisionNode != null
                ? (AtkResNode*)inputComponent->CollisionNode
                : (AtkResNode*)inputNode;
            state.InputFocusEvent = Dalamud.AddonEventManager.AddEvent(
                (nint)addon,
                (nint)inputEventNode,
                AddonEventType.FocusStop,
                HandleRecipeNoteInputFocusStop);
            addon->UldManager.UpdateDrawNodeList();
            addon->UpdateCollisionNodeList(false);
            return true;
        }
        catch (Exception ex)
        {
            ReleaseState(ref _recipeNoteState, addonFinalizing: false);
            GatherBuddy.Log.Warning($"[NativeRecipeCrafting] RecipeNote native controls unavailable: {ex.Message}");
            return false;
        }
    }

    private bool EnsureCosmicState(AtkUnitBase* addon, AtkComponentNode* synthNode)
    {
        if (_cosmicState?.AddonAddress == (nint)addon)
            return true;
        if (_cosmicState != null)
            ReleaseState(ref _cosmicState, addonFinalizing: false);

        try
        {
            var buttonNode = GetOrDuplicateComponentNode(addon, synthNode, ButtonNodeIdOffset);
            if (buttonNode == null)
                throw new InvalidOperationException("The game did not duplicate the native WKSRecipeNotebook button.");
            if (buttonNode->Component == null || buttonNode->Component->GetComponentType() != ComponentType.Button)
                throw new InvalidOperationException("The duplicated WKSRecipeNotebook craft control is not a button.");

            var state = new InjectedAddonState
            {
                AddonName = CosmicRecipeAddonName,
                AddonAddress = (nint)addon,
                ButtonNode = buttonNode,
                InputNode = null,
                InputComponent = null,
                OriginalInputCallback = null,
            };
            _cosmicState = state;
            PositionCosmicButton(synthNode, buttonNode);
            AttachAfterTarget((AtkResNode*)buttonNode, (AtkResNode*)synthNode);
            var buttonEventNode = PrepareButtonEventNode(buttonNode);
            state.ButtonEvent = Dalamud.AddonEventManager.AddEvent(
                (nint)addon,
                (nint)buttonEventNode,
                AddonEventType.MouseClick,
                HandleCosmicButtonClick);
            if (state.ButtonEvent == null)
                throw new InvalidOperationException("The WKSRecipeNotebook craft button event could not be registered.");
            addon->UldManager.UpdateDrawNodeList();
            addon->UpdateCollisionNodeList(false);
            return true;
        }
        catch (Exception ex)
        {
            ReleaseState(ref _cosmicState, addonFinalizing: false);
            GatherBuddy.Log.Warning($"[NativeRecipeCrafting] WKSRecipeNotebook native button unavailable: {ex.Message}");
            return false;
        }
    }

    private void HandleRecipeNoteButtonClick(AddonEventType _, AddonEventData __)
    {
        if (_recipeNoteState == null
            || !TryValidateLiveSelection(RecipeNoteAddonName, out var recipeId, out var maximum))
            return;

        _recipeNoteState.Amount.Refresh(recipeId, maximum);
        StartCraft(recipeId, Math.Min(_recipeNoteState.Amount.Value, maximum));
    }

    private void HandleCosmicButtonClick(AddonEventType _, AddonEventData __)
    {
        if (!TryValidateLiveSelection(CosmicRecipeAddonName, out var recipeId, out var maximum))
            return;

        StartCraft(recipeId, maximum);
    }

    private void HandleRecipeNoteInputFocusStop(AddonEventType _, AddonEventData __)
    {
        if (_recipeNoteState == null)
            return;

        PollInputText(_recipeNoteState);
        SetInputText(_recipeNoteState, _recipeNoteState.Amount.Value.ToString());
    }

    private static bool TryValidateLiveSelection(string addonName, out uint recipeId, out int maximum)
    {
        recipeId = 0;
        maximum = 0;
        if (IPCSubscriber.IsReady("Artisan") || CraftingGatherBridge.HasActiveQueue)
            return false;
        if (!TryGetSelectedRecipe(out recipeId, out var requiredJob)
            || !CraftingGameInterop.ManualTakeoverMatchesRecipeClass(requiredJob, Dalamud.Objects.LocalPlayer?.ClassJob.RowId ?? 0))
            return false;
        if (Dalamud.GameGui.GetAddonByName(addonName) is not { Address: not 0 } addon)
            return false;

        if (addonName == RecipeNoteAddonName)
            return TryReadRecipeNoteCraftableCount((AddonRecipeNote*)addon.Address, out maximum);
        return TryReadDisplayedCount(
            ((AtkUnitBase*)addon.Address)->GetTextNodeById(CosmicCraftableCountNodeId),
            out maximum);
    }

    private static void StartCraft(uint recipeId, int amount)
    {
        if (NativeRecipeCraftingLauncher.TryStart(recipeId, amount, out var failure))
            return;

        failure = failure.Replace('\n', ' ');
        GatherBuddy.Log.Warning(
            $"[NativeRecipeCrafting] Rejected native craft: recipe={recipeId}, amount={amount}: {failure}");
        Dalamud.Chat.PrintError($"[GatherBuddy Ascended] {failure}");
    }

    private static bool TryGetSelectedRecipe(out uint recipeId, out uint requiredJob)
    {
        recipeId = 0;
        requiredJob = 0;
        var selected = RecipeNoteExt.GetSelectedRecipeEntry();
        if (selected == null || selected->RecipeId == 0 || selected->CraftType > 7)
            return false;

        recipeId = selected->RecipeId;
        requiredJob = (uint)selected->CraftType + 8;
        return true;
    }

    private static bool TryReadRecipeNoteCraftableCount(AddonRecipeNote* addon, out int count)
    {
        count = 0;
        return addon != null
            && addon->SelectedRecipeQuantityCraftableFromMaterialsInInventory != null
            && TryReadDisplayedCount(addon->SelectedRecipeQuantityCraftableFromMaterialsInInventory, out count);
    }

    internal static bool TryParseDisplayedCount(string? text, out int count)
    {
        count = 0;
        if (string.IsNullOrEmpty(text))
            return false;

        var foundDigit = false;
        foreach (var character in text)
        {
            if (character is < '0' or > '9')
                continue;
            foundDigit = true;
            var digit = character - '0';
            if (count > (int.MaxValue - digit) / 10)
            {
                count = int.MaxValue;
                return true;
            }
            count = count * 10 + digit;
        }
        return foundDigit;
    }

    private static bool TryReadDisplayedCount(AtkTextNode* textNode, out int count)
        => TryParseDisplayedCount(textNode == null ? null : textNode->NodeText.ToString(), out count);

    private static AtkComponentNode* GetOrDuplicateComponentNode(
        AtkUnitBase* addon,
        AtkComponentNode* source,
        uint nodeIdOffset)
    {
        var expectedNodeId = source->NodeId + nodeIdOffset;
        var existingNode = addon->UldManager.SearchNodeById(expectedNodeId);
        var existing = existingNode == null ? null : existingNode->GetAsAtkComponentNode();
        if (existing != null)
            return existing;

        var duplicated = addon->UldManager.DuplicateComponentNode(source->NodeId, 1, nodeIdOffset);
        return duplicated == null ? null : duplicated->GetAsAtkComponentNode();
    }

    private static void PositionRecipeNoteControls(
        AtkComponentNode* synthNode,
        AtkComponentNode* buttonNode,
        AtkComponentNode* inputNode)
    {
        var buttonY = synthNode->Y - 32;
        buttonNode->SetPositionFloat(synthNode->X, buttonY);
        inputNode->SetPositionFloat(
            synthNode->X - inputNode->Width - 4,
            buttonY + Math.Max(0, (buttonNode->Height - inputNode->Height) / 2f));
    }

    private static void PositionCosmicButton(AtkComponentNode* synthNode, AtkComponentNode* buttonNode)
        => buttonNode->SetPositionFloat(synthNode->X - buttonNode->Width, synthNode->Y);

    private static AtkResNode* PrepareButtonEventNode(AtkComponentNode* buttonNode)
    {
        buttonNode->ClearEvents();
        var eventNode = buttonNode->Component->UldManager.RootNode;
        if (eventNode == null)
            throw new InvalidOperationException("The duplicated native craft button has no collision root.");
        ClearEventsRecursively(eventNode);
        return eventNode;
    }

    private static void ClearEventsRecursively(AtkResNode* node)
    {
        node->ClearEvents();
        for (var child = node->ChildNode; child != null; child = child->PrevSiblingNode)
            ClearEventsRecursively(child);
    }

    private static void AttachAfterTarget(AtkResNode* node, AtkResNode* target)
    {
        var parent = target->ParentNode;
        if (parent == null)
            throw new InvalidOperationException("The native synthesis control has no parent node.");
        if (node->ParentNode != null)
        {
            if (node->ParentNode == parent)
                return;
            DetachNode(node);
        }

        node->ParentNode = parent;
        if (target->PrevSiblingNode != null)
        {
            target->PrevSiblingNode->NextSiblingNode = node;
            node->PrevSiblingNode = target->PrevSiblingNode;
        }
        target->PrevSiblingNode = node;
        node->NextSiblingNode = target;
        if (parent->GetNodeType() != NodeType.Component)
            parent->ChildCount++;
    }

    private static void DetachNode(AtkResNode* node)
    {
        if (node == null || node->ParentNode == null)
            return;
        var parent = node->ParentNode;
        if (parent->ChildNode == node)
            parent->ChildNode = node->PrevSiblingNode != null
                ? node->PrevSiblingNode
                : node->NextSiblingNode;
        if (node->PrevSiblingNode != null)
            node->PrevSiblingNode->NextSiblingNode = node->NextSiblingNode;
        if (node->NextSiblingNode != null)
            node->NextSiblingNode->PrevSiblingNode = node->PrevSiblingNode;
        if (parent->GetNodeType() != NodeType.Component && parent->ChildCount > 0)
            parent->ChildCount--;
        node->ParentNode = null;
        node->PrevSiblingNode = null;
        node->NextSiblingNode = null;
    }

    private static void PollInputText(InjectedAddonState state)
    {
        var text = state.InputComponent->RawString.ToString();
        if (string.Equals(text, state.LastInputText, StringComparison.Ordinal))
            return;
        state.LastInputText = text;

        if (!state.Amount.ApplyText(text))
        {
            SetInputText(state, state.Amount.Value.ToString());
            return;
        }
        if (text.Length > 0 && !string.Equals(text, state.Amount.Value.ToString(), StringComparison.Ordinal))
            SetInputText(state, state.Amount.Value.ToString());
    }

    private static void SetInputText(InjectedAddonState state, string text)
    {
        state.InputComponent->SetText(text);
        state.LastInputText = text;
    }

    private static void SetStateVisibility(InjectedAddonState? state, bool visible, bool enabled)
    {
        if (state == null)
            return;
        state.ButtonNode->ToggleVisibility(visible);
        state.ButtonNode->Component->SetEnabledState(enabled);
        if (state.InputNode != null)
        {
            state.InputNode->ToggleVisibility(visible);
            state.InputNode->Component->SetEnabledState(enabled);
        }
    }

    private static void RemoveEvent(IAddonEventHandle? handle)
    {
        if (handle == null)
            return;
        try
        {
            Dalamud.AddonEventManager.RemoveEvent(handle);
        }
        catch (Exception ex)
        {
            GatherBuddy.Log.Debug($"[NativeRecipeCrafting] Native event cleanup was already complete: {ex.Message}");
        }
    }

    private static void ReleaseState(ref InjectedAddonState? state, bool addonFinalizing)
    {
        if (state == null)
            return;

        RemoveEvent(state.ButtonEvent);
        RemoveEvent(state.InputFocusEvent);
        var canTouchNative = addonFinalizing;
        if (!canTouchNative
            && Dalamud.GameGui.GetAddonByName(state.AddonName) is { Address: not 0 } liveAddon
            && liveAddon.Address == state.AddonAddress)
            canTouchNative = true;

        if (canTouchNative)
        {
            if (state.InputComponent != null)
                state.InputComponent->Callback = state.OriginalInputCallback;
            if (!addonFinalizing)
            {
                var addon = (AtkUnitBase*)state.AddonAddress;
                SetStateVisibility(state, visible: false, enabled: false);
                DetachNode((AtkResNode*)state.InputNode);
                DetachNode((AtkResNode*)state.ButtonNode);
                addon->UldManager.UpdateDrawNodeList();
                addon->UpdateCollisionNodeList(false);
            }
        }
        state = null;
    }

    [UnmanagedCallersOnly]
    private static InputCallbackResult SuppressRecipeSearchCallback(
        AtkUnitBase* _,
        InputCallbackType __,
        CStringPointer ___,
        CStringPointer ____,
        int _____)
        => InputCallbackResult.None;
}
