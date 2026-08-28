using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
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

        var availableHeight = Math.Max(1f, ImGui.GetContentRegionAvail().Y);
        var controlsHeight = ImGui.GetFrameHeightWithSpacing();
        var panelHeight = Math.Max(1f, availableHeight - controlsHeight - ImGui.GetStyle().ItemSpacing.Y * 4f);
        var plannerHeight = panelHeight * 0.52f;
        var estimateHeight = panelHeight * 0.20f;
        var targetsHeight = panelHeight - plannerHeight - estimateHeight;

        DrawPlanner(manager, list, plannerHeight);
        DrawPlannerFooter(manager, list);
        ImGui.Separator();
        ImGui.BeginChild("##marketplaceEstimate", new Vector2(0, estimateHeight), true,
            ImGuiWindowFlags.HorizontalScrollbar);
        DrawEstimate(manager, list);
        ImGui.EndChild();
        ImGui.Separator();
        ImGui.BeginChild("##marketplaceTargets", new Vector2(0, targetsHeight), true,
            ImGuiWindowFlags.HorizontalScrollbar);
        DrawInventoryTargets(manager, list);
        ImGui.EndChild();
    }

    private static void DrawPlanner(MarketplaceBuyListManager manager, MarketplaceBuyListDefinition list, float height)
    {
        var snapshot = manager.Snapshot;
        var availability = snapshot?.Input.Dependencies.ToDictionary(
            dependency => dependency.ItemId,
            dependency => ResolveMarketAvailability(snapshot, list, dependency.ItemId))
            ?? new System.Collections.Generic.Dictionary<uint, MarketAvailability>();
        using (ImRaii.Disabled(list.IsReadOnly || manager.IsBusy))
        {
            CraftingPurchaseConfigurationWindow.DrawPurchaseTable(
                new CraftingPurchaseConfigurationWindow.PurchaseTableContext(
                    snapshot,
                    availability,
                    manager.Planning?.SelectedPlan,
                    itemId => list.PurchaseItemPolicies.TryGetValue(itemId, out var policy) ? policy : null,
                    (itemId, policy) => manager.SetItemPolicy(list.Id, itemId, policy),
                    "##marketplacePurchaseConfigurationTable"),
                height);
        }
    }

    private static MarketAvailability ResolveMarketAvailability(
        AcquisitionPlanningInputBuilder.BuildResult snapshot,
        MarketplaceBuyListDefinition list,
        uint itemId)
    {
        if (snapshot.IsLoading)
            return new MarketAvailability(itemId, MarketAvailabilityState.Unknown, snapshot.LoadingReason);
        var service = GatherBuddy.MarketboardService;
        var scope = list.CurrentWorldOnly ? service?.GetCurrentWorld() : service?.GetDataCenter();
        if (service != null && !string.IsNullOrWhiteSpace(scope) && service.HasError(itemId, scope))
            return new MarketAvailability(itemId, MarketAvailabilityState.Unknown, "Market availability could not be checked.");
        return snapshot.Input.MarketListings.Any(listing => listing.ItemId == itemId && listing.IsAvailable)
            ? new MarketAvailability(itemId, MarketAvailabilityState.Available, "Available on the market.")
            : new MarketAvailability(itemId, MarketAvailabilityState.Unavailable, "No market listing is available.");
    }

    private static void DrawPlannerFooter(MarketplaceBuyListManager manager, MarketplaceBuyListDefinition list)
    {
        var maxGil = (int)Math.Clamp(list.MaximumGilSpend ?? 0, 0, int.MaxValue);
        ImGui.SetNextItemWidth(VulcanUiScaling.Scaled(150f));
        using (ImRaii.Disabled(list.IsReadOnly || manager.IsBusy))
        if (ImGui.InputInt("Max Gil##marketplaceMaxGil", ref maxGil))
            manager.UpdateSettings(list.Id,
                maximumGilSpend: Math.Max(0, maxGil),
                clearMaximumGilSpend: maxGil <= 0);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("0 means unlimited.");
        ImGui.SameLine();
        var currentWorldOnly = list.CurrentWorldOnly;
        using (ImRaii.Disabled(list.IsReadOnly || manager.IsBusy))
        if (ImGui.Checkbox("Current World Only##marketplaceCurrentWorld", ref currentWorldOnly))
            manager.UpdateSettings(list.Id, currentWorldOnly: currentWorldOnly);

        var refreshWidth = ImGui.CalcTextSize("Refresh Estimate").X + ImGui.GetStyle().FramePadding.X * 2f;
        ImGui.SameLine(Math.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - refreshWidth));
        using (ImRaii.Disabled(manager.IsBusy))
        if (ImGui.Button("Refresh Estimate"))
            manager.RefreshEstimate();
    }

    private static void DrawInventoryTargets(MarketplaceBuyListManager manager, MarketplaceBuyListDefinition list)
    {
        ImGui.TextColored(ImGuiColors.DalamudGrey3, "Inventory targets (Have / Target / Need)");
        foreach (var entry in list.Entries.ToArray())
        {
            ImGui.PushID($"marketplaceEntry_{entry.ItemId}");
            ImGui.TextUnformatted(entry.ItemName.Length == 0 ? $"Item #{entry.ItemId}" : entry.ItemName);
            ImGui.SameLine();
            var have = Math.Max(0, Vulcan.Vendors.VendorBuyListManager.GetCurrentInventoryAndArmoryCount(entry.ItemId));
            var target = entry.TargetQuantity;
            ImGui.SetNextItemWidth(VulcanUiScaling.Scaled(90f));
            using (ImRaii.Disabled(list.IsReadOnly || manager.IsBusy))
            if (ImGui.InputInt("##target", ref target))
                manager.SetTarget(list.Id, entry.ItemId, target);
            ImGui.SameLine();
            ImGui.TextColored(ImGuiColors.DalamudGrey3,
                $"{have:N0} / {Math.Max(0, target):N0} / {Math.Max(0, target - have):N0}");
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
            {
                if (manager.Start())
                    GatherBuddy.CraftingStatusWindow?.SetMarketplaceBuyListManager(manager);
            }
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
        var snapshot = manager.Snapshot;
        if (snapshot?.IsLoading == true)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, "Estimate");
            ImGui.TextColored(ImGuiColors.DalamudGrey3, snapshot.LoadingReason);
            return;
        }
        var planning = manager.Planning;
        if (!manager.IsEstimateReady)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, "Estimate");
            ImGui.TextColored(ImGuiColors.DalamudGrey3, "Refreshing estimate...");
            return;
        }
        if (planning == null)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, "Estimate");
            ImGui.TextColored(ImGuiColors.DalamudGrey3, "Waiting for game/vendor/market data.");
            return;
        }
        if (!planning.IsSuccess)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, "Estimate");
            ImGui.TextColored(ImGuiColors.DalamudYellow, manager.StatusText);
            return;
        }
        var plan = planning.SelectedPlan;
        if (plan == null)
        {
            ImGui.TextColored(ImGuiColors.DalamudYellow, "Estimate");
            ImGui.TextColored(ImGuiColors.DalamudGrey3, list.Entries.Count == 0 ? "List is empty." : "All targets already satisfied.");
            return;
        }
        ImGui.TextColored(ImGuiColors.DalamudYellow, "Estimate:");
        foreach (var currency in plan.Estimate.Currencies)
        {
            ImGui.SameLine(0, VulcanUiScaling.Scaled(10f));
            CraftingPurchasePlanWindow.DrawCurrency(currency.IconId, currency.Required, currency.CurrencyName);
        }
        foreach (var row in CraftingPurchasePlanWindow.BuildItemEstimates(plan))
            CraftingPurchasePlanWindow.DrawEstimateItem(row);
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
