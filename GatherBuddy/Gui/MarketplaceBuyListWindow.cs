using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using ElliLib.Raii;
using GatherBuddy.Crafting.Acquisition;
using GatherBuddy.Marketboard;
using ImRaii = ElliLib.Raii.ImRaii;

namespace GatherBuddy.Gui;

public sealed class MarketplaceBuyListWindow : Window, IDisposable
{
    public const string WindowId = "Buy List###MarketplaceBuyListWindow";

    private string _renameInput = string.Empty;
    private string _itemSearch = string.Empty;
    private int _addQuantity = 1;

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
        using (ImRaii.Disabled(manager.IsBusy))
        {
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
        }
        ImGui.Separator();
        foreach (var list in manager.Lists.OrderBy(list => list.CreatedAt))
        {
            using (ImRaii.Disabled(manager.IsBusy))
                if (ImGui.Selectable($"{list.Name}##marketplaceList_{list.Id}", list.Id == active.Id))
                    manager.SelectList(list.Id);
        }
    }

    private void DrawEditor(MarketplaceBuyListManager manager, MarketplaceBuyListDefinition list)
    {
        ImGui.TextColored(ImGuiColors.DalamudYellow, list.Name);
        ImGui.SameLine();
        ImGui.TextColored(ImGuiColors.DalamudGrey3, $"{list.Entries.Count} item(s)");
        ImGui.Separator();
        DrawActions(manager, list);
        ImGui.Separator();
        DrawItemAdder(manager, list);
        ImGui.Separator();
        var preferHq = list.PreferHQ;
        using (ImRaii.Disabled(list.IsReadOnly || manager.IsBusy))
        if (ImGui.Checkbox("Prefer HQ", ref preferHq))
            manager.UpdateSettings(list.Id, preferHq: preferHq);
        var currentWorldOnly = list.CurrentWorldOnly;
        using (ImRaii.Disabled(list.IsReadOnly || manager.IsBusy))
        if (ImGui.Checkbox("Current world only", ref currentWorldOnly))
            manager.UpdateSettings(list.Id, currentWorldOnly: currentWorldOnly);
        var preferVendors = list.PreferVendors;
        using (ImRaii.Disabled(list.IsReadOnly || manager.IsBusy))
        if (ImGui.Checkbox("Prefer vendors", ref preferVendors))
            manager.UpdateSettings(list.Id, preferVendors: preferVendors);
        var preferCurrency = list.PreferMarketForSpecialCurrency;
        using (ImRaii.Disabled(list.IsReadOnly || manager.IsBusy))
        if (ImGui.Checkbox("Prefer market for special currency", ref preferCurrency))
            manager.UpdateSettings(list.Id, preferMarketForSpecialCurrency: preferCurrency);
        var maxSpend = list.MaximumGilSpend is long value ? (int)Math.Clamp(value, 0, int.MaxValue) : 0;
        var hasMax = list.MaximumGilSpend.HasValue;
        using (ImRaii.Disabled(list.IsReadOnly || manager.IsBusy))
        if (ImGui.Checkbox("Set maximum Gil spend", ref hasMax))
            manager.UpdateSettings(list.Id, clearMaximumGilSpend: !hasMax);
        if (hasMax)
        {
            ImGui.SetNextItemWidth(VulcanUiScaling.Scaled(180f));
            using (ImRaii.Disabled(list.IsReadOnly || manager.IsBusy))
            if (ImGui.InputInt("Maximum Gil", ref maxSpend))
                manager.UpdateSettings(list.Id, maximumGilSpend: Math.Max(0, maxSpend));
        }

        ImGui.Spacing();
        ImGui.Separator();
        DrawEstimate(manager, list);
        ImGui.Separator();
        ImGui.TextColored(ImGuiColors.DalamudGrey3, "Inventory targets (Have / Target / Need)");
        foreach (var entry in list.Entries.ToArray())
        {
            ImGui.PushID($"marketplaceEntry_{entry.ItemId}");
            ImGui.Text(entry.ItemName.Length == 0 ? $"Item #{entry.ItemId}" : entry.ItemName);
            ImGui.SameLine();
            var have = Math.Max(0, Vulcan.Vendors.VendorBuyListManager.GetCurrentInventoryAndArmoryCount(entry.ItemId));
            var target = entry.TargetQuantity;
            ImGui.SetNextItemWidth(VulcanUiScaling.Scaled(90f));
            using (ImRaii.Disabled(list.IsReadOnly || manager.IsBusy))
            if (ImGui.InputInt("##target", ref target))
                manager.SetTarget(list.Id, entry.ItemId, target);
            ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.DalamudGrey3, $"{have:N0} / {Math.Max(0, target):N0} / {Math.Max(0, target - have):N0}");
            ImGui.SameLine();
            using (ImRaii.Disabled(list.IsReadOnly || manager.IsBusy))
            if (ImGui.SmallButton("Remove"))
                manager.RemoveItem(list.Id, entry.ItemId);
            ImGui.PopID();
        }
        if (list.Entries.Count == 0)
            ImGui.TextColored(ImGuiColors.DalamudGrey3, "No items. Search above or add from the Marketboard tab.");
    }

    private void DrawActions(MarketplaceBuyListManager manager, MarketplaceBuyListDefinition list)
    {
        if (manager.IsBusy)
        {
            if (ImGui.Button("Stop", VulcanUiScaling.Scaled(120f, 0f)))
                manager.Stop();
            ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.DalamudYellow, $"{manager.Stage}: {manager.StatusText}");
        }
        else
        {
            using (ImRaii.Disabled(list.IsReadOnly || list.Entries.Count == 0))
            if (ImGui.Button("Start List", VulcanUiScaling.Scaled(120f, 0f)))
                manager.Start();
            ImGui.SameLine();
            using (ImRaii.Disabled(list.IsReadOnly || list.Entries.Count == 0))
            if (ImGui.Button("Clear List", VulcanUiScaling.Scaled(120f, 0f)))
                manager.Clear();
            ImGui.SameLine();
            if (manager.LastResult is { } lastResult)
            {
                var color = lastResult.Status == LiveAcquisitionStatus.Completed
                    ? ImGuiColors.HealerGreen
                    : ImGuiColors.DalamudRed;
                ImGui.TextColored(color, $"Last run: {lastResult.Message}");
            }
            else
            {
                ImGui.TextColored(ImGuiColors.DalamudGrey3, manager.StatusText);
            }
        }
    }

    private void DrawItemAdder(MarketplaceBuyListManager manager, MarketplaceBuyListDefinition list)
    {
        ImGui.TextColored(ImGuiColors.DalamudYellow, "Add item");
        using (ImRaii.Disabled(list.IsReadOnly || manager.IsBusy))
        {
            ImGui.SetNextItemWidth(VulcanUiScaling.Scaled(260f));
            ImGui.InputTextWithHint("##marketplaceItemSearch", "Search items", ref _itemSearch, 128);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(VulcanUiScaling.Scaled(80f));
            ImGui.InputInt("Qty", ref _addQuantity);
        }

        if (list.IsReadOnly || manager.IsBusy || string.IsNullOrWhiteSpace(_itemSearch))
            return;
        var service = GatherBuddy.MarketboardService;
        if (service == null)
            return;
        var results = service.SearchItems(_itemSearch, 20, includeNonMarketable: true);
        if (results.Count == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey3, "No matching items.");
            return;
        }
        foreach (var result in results)
        {
            ImGui.PushID($"marketplaceSearch_{result.ItemId}");
            ImGui.TextUnformatted(result.Name);
            ImGui.SameLine();
            if (ImGui.SmallButton("Add"))
            {
                manager.AddItem(list.Id, result.ItemId, result.Name, result.IconId, Math.Max(1, _addQuantity));
                _itemSearch = string.Empty;
            }
            ImGui.PopID();
        }
    }

    private static void DrawEstimate(MarketplaceBuyListManager manager, MarketplaceBuyListDefinition list)
    {
        ImGui.TextColored(ImGuiColors.DalamudYellow, "Estimate");
        var snapshot = manager.Snapshot;
        if (snapshot?.IsLoading == true)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey3, snapshot.LoadingReason);
            return;
        }
        var planning = manager.Planning;
        if (!manager.IsEstimateReady)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey3, "Refreshing estimate...");
            return;
        }
        if (planning == null)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey3, "Waiting for game/vendor/market data.");
            return;
        }
        if (!planning.IsSuccess)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, manager.StatusText);
            return;
        }
        var preferred = planning.PreferredEstimate;
        var minimum = planning.MinimumGilEstimate;
        if (preferred == null && minimum == null)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey3, list.Entries.Count == 0 ? "List is empty." : "All targets already satisfied.");
            return;
        }
        if (preferred != null)
            ImGui.TextColored(ImGuiColors.ParsedGold, $"Preferred estimate: {preferred.TotalGil:N0} Gil");
        if (minimum != null && (preferred == null || minimum.TotalGil != preferred.TotalGil))
            ImGui.TextColored(ImGuiColors.DalamudGrey3, $"Minimum-Gil estimate: {minimum.TotalGil:N0} Gil");

        var estimate = preferred ?? minimum!;
        foreach (var currency in estimate.Currencies.Where(currency => !currency.IsSpecialCurrency || currency.Required > 0))
        {
            var color = currency.Available >= currency.Required ? ImGuiColors.HealerGreen : ImGuiColors.DalamudYellow;
            if (currency.IconId != 0)
            {
                var icon = Icons.DefaultStorage.TextureProvider.GetFromGameIcon(new GameIconLookup(currency.IconId));
                if (icon.TryGetWrap(out var wrap, out _))
                {
                    var iconSize = VulcanUiScaling.Scaled(18f);
                    ImGui.Image(wrap.Handle, new Vector2(iconSize, iconSize));
                    ImGui.SameLine(0, VulcanUiScaling.Scaled(4f));
                }
            }
            ImGui.TextColored(color, $"{currency.CurrencyName}: {currency.Available:N0} / {currency.Required:N0}");
        }
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
