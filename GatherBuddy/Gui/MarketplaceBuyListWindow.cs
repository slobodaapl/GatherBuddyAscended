using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Windowing;
using ElliLib.Raii;
using GatherBuddy.Marketboard;
using ImRaii = ElliLib.Raii.ImRaii;

namespace GatherBuddy.Gui;

public sealed class MarketplaceBuyListWindow : Window, IDisposable
{
    public const string WindowId = "Marketplace Buy List###MarketplaceBuyListWindow";

    private string _renameInput = string.Empty;

    public MarketplaceBuyListWindow() : base(WindowId)
    {
        Size = VulcanUiScaling.Scaled(860f, 520f);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = VulcanUiScaling.Scaled(620f, 320f),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        IsOpen = false;
    }

    public void Open() => IsOpen = true;

    public void OpenList(Guid id)
    {
        GatherBuddy.MarketplaceBuyListManager?.SelectList(id);
        IsOpen = true;
    }

    public override void Draw()
    {
        var manager = GatherBuddy.MarketplaceBuyListManager;
        if (manager == null)
            return;
        var active = manager.ActiveList;
        if (active == null)
            return;

        ImGui.TextColored(ImGuiColors.DalamudGrey3, "Persistent marketplace purchase lists.");
        ImGui.Spacing();

        var avail = ImGui.GetContentRegionAvail();
        var leftWidth = VulcanUiScaling.Scaled(210f);
        ImGui.BeginChild("##marketplaceBuyListSidebar", new Vector2(leftWidth, avail.Y), true);
        DrawSidebar(manager, active);
        ImGui.EndChild();
        ImGui.SameLine();
        ImGui.BeginChild("##marketplaceBuyListEditor", new Vector2(0, avail.Y), true);
        DrawEditor(manager, active);
        ImGui.EndChild();

        DrawRenamePopup(manager);
    }

    private static void DrawSidebar(MarketplaceBuyListManager manager, MarketplaceBuyListDefinition active)
    {
        ImGui.TextColored(ImGuiColors.DalamudYellow, "Lists");
        ImGui.TextColored(ImGuiColors.DalamudGrey3, $"{manager.Lists.Count} total");
        ImGui.Spacing();
        if (ImGui.Button("New List", new Vector2(-1, 0)))
        {
            var list = manager.CreateList();
            GatherBuddy.MarketplaceBuyListWindow?.BeginRename(list);
        }
        var half = (ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ItemSpacing.X) / 2f;
        if (ImGui.Button("Rename", new Vector2(half, 0)))
            GatherBuddy.MarketplaceBuyListWindow?.BeginRename(active);
        ImGui.SameLine();
        using (ImRaii.Disabled(manager.Lists.Count <= 1))
            if (ImGui.Button("Delete", new Vector2(half, 0)))
                manager.DeleteList(active.Id);
        ImGui.Separator();
        foreach (var list in manager.Lists.OrderBy(list => list.CreatedAt))
        {
            if (ImGui.Selectable($"{list.Name}##marketplaceList_{list.Id}", list.Id == active.Id))
                manager.SelectList(list.Id);
        }
    }

    private static void DrawEditor(MarketplaceBuyListManager manager, MarketplaceBuyListDefinition list)
    {
        ImGui.TextColored(ImGuiColors.DalamudYellow, list.Name);
        ImGui.Separator();
        var preferHq = list.PreferHQ;
        if (ImGui.Checkbox("Prefer HQ", ref preferHq))
            manager.UpdateSettings(list.Id, preferHq: preferHq);
        var currentWorldOnly = list.CurrentWorldOnly;
        if (ImGui.Checkbox("Current world only", ref currentWorldOnly))
            manager.UpdateSettings(list.Id, currentWorldOnly: currentWorldOnly);
        var preferVendors = list.PreferVendors;
        if (ImGui.Checkbox("Prefer vendors", ref preferVendors))
            manager.UpdateSettings(list.Id, preferVendors: preferVendors);
        var preferCurrency = list.PreferMarketForSpecialCurrency;
        if (ImGui.Checkbox("Prefer market for special currency", ref preferCurrency))
            manager.UpdateSettings(list.Id, preferMarketForSpecialCurrency: preferCurrency);
        var maxSpend = list.MaximumGilSpend is long value ? (int)Math.Clamp(value, 0, int.MaxValue) : 0;
        var hasMax = list.MaximumGilSpend.HasValue;
        if (ImGui.Checkbox("Set maximum Gil spend", ref hasMax))
            manager.UpdateSettings(list.Id, clearMaximumGilSpend: !hasMax);
        if (hasMax)
        {
            ImGui.SetNextItemWidth(VulcanUiScaling.Scaled(180f));
            if (ImGui.InputInt("Maximum Gil", ref maxSpend))
                manager.UpdateSettings(list.Id, maximumGilSpend: Math.Max(0, maxSpend));
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextColored(ImGuiColors.DalamudGrey3, "Inventory targets");
        foreach (var entry in list.Entries.ToArray())
        {
            ImGui.PushID($"marketplaceEntry_{entry.ItemId}");
            ImGui.Text(entry.ItemName.Length == 0 ? $"Item #{entry.ItemId}" : entry.ItemName);
            ImGui.SameLine();
            var target = entry.TargetQuantity;
            ImGui.SetNextItemWidth(VulcanUiScaling.Scaled(90f));
            if (ImGui.InputInt("##target", ref target))
                manager.SetTarget(list.Id, entry.ItemId, target);
            ImGui.SameLine();
            if (ImGui.SmallButton("Remove"))
                manager.RemoveItem(list.Id, entry.ItemId);
            ImGui.PopID();
        }
        if (list.Entries.Count == 0)
            ImGui.TextColored(ImGuiColors.DalamudGrey3, "No items. Add items from the Marketboard tab.");
    }

    public void BeginRename(MarketplaceBuyListDefinition list)
    {
        _renamePopupId = list.Id;
        _renamePopupOpen = true;
        _renameInput = list.Name;
        ImGui.OpenPopup("##marketplaceRenamePopup");
    }

    private void DrawRenamePopup(MarketplaceBuyListManager manager)
    {
        if (!ImGui.BeginPopupModal("##marketplaceRenamePopup", ref _renamePopupOpen,
                ImGuiWindowFlags.AlwaysAutoResize))
            return;
        ImGui.Text("List name");
        ImGui.SetNextItemWidth(VulcanUiScaling.Scaled(280f));
        ImGui.InputText("##marketplaceRename", ref _renameInput, 128);
        if (ImGui.Button("Save") && _renamePopupId.HasValue)
        {
            manager.RenameList(_renamePopupId.Value, _renameInput);
            _renamePopupOpen = false;
            _renamePopupId = null;
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
        {
            _renamePopupOpen = false;
            _renamePopupId = null;
        }
        ImGui.EndPopup();
    }

    private bool _renamePopupOpen;
    private Guid? _renamePopupId;

    public void Dispose() { }
}
