using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GatherBuddy.Automation;

namespace GatherBuddy.Crafting;

[StructLayout(LayoutKind.Explicit, Size = 0x88)]
public unsafe struct RecipeNoteIngredientEntry
{
    [FieldOffset(0x04)] public ushort NumAvailableNQ;
    [FieldOffset(0x06)] public ushort NumAvailableHQ;
    [FieldOffset(0x08)] public byte NumAssignedNQ;
    [FieldOffset(0x09)] public byte NumAssignedHQ;
    [FieldOffset(0x78)] public uint ItemId;
    [FieldOffset(0x82)] public byte NumTotal;

    public void SetMaxHQ(bool updateUI = true)
    {
        NumAssignedNQ = 0;
        NumAssignedHQ = (byte)Math.Min(NumAvailableHQ, NumTotal);

        GatherBuddy.Log.Debug($"[Crafting] SetMaxHQ: assigned NQ={NumAssignedNQ}, assigned HQ={NumAssignedHQ}");

        if (updateUI && Dalamud.GameGui.GetAddonByName("RecipeNote") is { Address: not 0 } addon)
        {
            Callback.Fire((AtkUnitBase*)addon.Address, true, 6);
        }
    }

    public void SetMaxNQ(bool updateUI = true)
    {
        NumAssignedNQ = (byte)Math.Min(NumAvailableNQ, NumTotal);
        NumAssignedHQ = 0;

        GatherBuddy.Log.Debug($"[Crafting] SetMaxNQ: assigned NQ={NumAssignedNQ}, assigned HQ={NumAssignedHQ}");

        if (updateUI && Dalamud.GameGui.GetAddonByName("RecipeNote") is { Address: not 0 } addon)
        {
            Callback.Fire((AtkUnitBase*)addon.Address, true, 6);
        }
    }

    public void SetSpecific(int nq, int hq, bool updateUI = true)
    {
        if (NumAvailableNQ + NumAvailableHQ < NumTotal)
        {
            GatherBuddy.Log.Warning($"[Crafting] Unable to set specified ingredients properly due to insufficient materials.");
            return;
        }

        NumAssignedNQ = 0;
        NumAssignedHQ = 0;

        NumAssignedNQ = (byte)Math.Min(NumTotal, Math.Min(NumAvailableNQ, nq));
        if (NumAssignedNQ != NumTotal)
            NumAssignedHQ = (byte)Math.Min(NumTotal, Math.Min(NumAvailableHQ, hq));

        if (updateUI && Dalamud.GameGui.GetAddonByName("RecipeNote") is { Address: not 0 } addon)
        {
            Callback.Fire((AtkUnitBase*)addon.Address, true, 6);
        }
    }
}

public static unsafe class RecipeNoteExt
{
    [StructLayout(LayoutKind.Explicit, Size = 0x400)]
    public struct RecipeEntry
    {
        [FieldOffset(0x000)] public fixed byte Ingredients[6 * 0x88];
        [FieldOffset(0x3B2)] public ushort RecipeId;
        [FieldOffset(0x3D7)] public byte CraftType;

        public Span<RecipeNoteIngredientEntry> IngredientsSpan
            => new(Unsafe.AsPointer(ref Ingredients[0]), 6);
    }

    [StructLayout(LayoutKind.Explicit, Size = 0x470)]
    public struct RecipeData
    {
        [FieldOffset(0x000)] public RecipeEntry* Recipes;
        [FieldOffset(0x008)] public int RecipesCount;
        [FieldOffset(0x468)] public ushort SelectedIndex;
    }

    public static RecipeData* GetRecipeData()
    {
        var recipeNote = RecipeNote.Instance();
        return recipeNote == null ? null : (RecipeData*)recipeNote->RecipeList;
    }

    public static RecipeEntry* GetSelectedRecipeEntry()
    {
        var data = GetRecipeData();
        return data != null
            && data->Recipes != null
            && data->SelectedIndex < data->RecipesCount
            ? data->Recipes + data->SelectedIndex
            : null;
    }

    public static uint? GetActiveCraftRecipeId()
    {
        var recipeNote = RecipeNote.Instance();
        if (recipeNote == null)
            return null;
        return recipeNote->ActiveCraftRecipeId == 0 ? null : recipeNote->ActiveCraftRecipeId;
    }

    public static Span<RecipeNoteIngredientEntry> GetIngredientsSpan(RecipeEntry* recipe)
        => recipe == null ? [] : recipe->IngredientsSpan;

    public static Span<RecipeNoteIngredientEntry> GetIngredientsSpan(FFXIVClientStructs.FFXIV.Client.Game.UI.RecipeNote.RecipeEntry* recipe)
    {
        if (recipe == null)
            return new Span<RecipeNoteIngredientEntry>();
        
        var ptr = (RecipeNoteIngredientEntry*)recipe;
        return new Span<RecipeNoteIngredientEntry>(ptr, 6);
    }
}
