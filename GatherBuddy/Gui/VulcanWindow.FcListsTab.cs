using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Colors;
using ElliLib.Raii;
using GatherBuddy.Crafting;
using GatherBuddy.FcMesh.Chest;
using GatherBuddy.FcMesh.Fulfillment;
using GatherBuddy.FcMesh.Native;
using GatherBuddy.FcMesh.Protocol;
using GatherBuddy.FcMesh.Publication;
using Lumina.Excel.Sheets;
using ImRaii = ElliLib.Raii.ImRaii;

namespace GatherBuddy.Gui;

public partial class VulcanWindow
{
    private readonly HashSet<Guid> _fcSelectedPublicLists = new();
    private bool _fcUseOwnStock;
    private string _fcJoinTicket = string.Empty;
    private string? _fcUiMessage;

    private void DrawFcListsTab()
    {
        IDisposable tabItem;
        bool tabOpen;

        if (GatherBuddy.ControllerSupport != null && !_fcListsTabRequestFocus)
        {
            var handle = GatherBuddy.ControllerSupport.TabNavigation.TabItem(
                "FC Lists##fcListsTab", FcListsTabIndex, VulcanTabCount);
            tabItem = handle;
            tabOpen = handle;
        }
        else
        {
            ImRaii.IEndObject handle;
            if (_fcListsTabRequestFocus)
            {
                bool dummy = true;
                handle = ImRaii.TabItem("FC Lists##fcListsTab", ref dummy, ImGuiTabItemFlags.SetSelected);
            }
            else
            {
                handle = ImRaii.TabItem("FC Lists##fcListsTab");
            }

            tabItem = handle;
            tabOpen = handle.Success;
            if (tabOpen)
                _fcListsTabRequestFocus = false;
        }

        using (tabItem)
        {
            if (!tabOpen)
                return;

            DrawFcListsTabContent();
        }
    }

    private void DrawFcListsTabContent()
    {
        if (_fcUiMessage is { Length: > 0 } actionMessage)
        {
            ImGui.TextColored(ImGuiColors.DalamudRed, actionMessage);
            if (ImGui.SmallButton("Dismiss##fcUiMessageDismiss"))
                _fcUiMessage = null;
            ImGui.Separator();
        }

        DrawFcGroupSection();
        if (GatherBuddy.FcMeshNative?.Readiness.State != FcMeshReadinessState.Ready)
            return;
        ImGui.Separator();
        DrawFcLocalCopiesSection();
        ImGui.Separator();
        var compatible = DrawFcPublicListsSection();
        ImGui.Separator();
        DrawFcFulfillmentSection(compatible);
        ImGui.Separator();
        DrawFcChestSection();
    }

    private void DrawFcGroupSection()
    {
        ImGui.TextColored(ImGuiColors.ParsedGold, "FC group");

        var mesh = GatherBuddy.FcMeshNative;
        var readiness = mesh?.Readiness;
        var state = readiness?.State ?? FcMeshReadinessState.NotCreated;
        ImGui.Text($"Status: {FormatFcReadinessState(state)}");

        if (state == FcMeshReadinessState.Error)
            ImGui.TextWrapped("FC group connection failed.");

        var connecting = state is FcMeshReadinessState.Joining or FcMeshReadinessState.ReconcilingSnapshot;
        var showSetup = state is FcMeshReadinessState.NotCreated
            or FcMeshReadinessState.Created
            or FcMeshReadinessState.Left
            or FcMeshReadinessState.Error;
        if (connecting)
        {
            ImGui.Text("Connecting...");
        }
        else if (showSetup)
        {
            using (ImRaii.Disabled(mesh is null))
            {
                if (ImGui.Button("Create FC group##fcCreateGroup"))
                    TryCreateFcGroup(mesh!);
                ImGui.Text("Join invite");
                ImGui.SetNextItemWidth(MathF.Min(360f, ImGui.GetContentRegionAvail().X));
                ImGui.InputText(
                    "##fcJoinInvite",
                    ref _fcJoinTicket,
                    4096,
                    ImGuiInputTextFlags.Password);
                if (ImGui.Button("Join FC group##fcJoinGroup") && !string.IsNullOrWhiteSpace(_fcJoinTicket))
                    TryJoinFcGroup(mesh!);
            }
        }

        if (state == FcMeshReadinessState.Ready
            && readiness?.GroupTicket is { Length: > 0 } groupTicket)
        {
            if (ImGui.Button("Copy Invite##fcCopyInvite"))
                ImGui.SetClipboardText(groupTicket);
        }

        if (state != FcMeshReadinessState.Ready)
            return;

        var leaveAllowed = CanLeaveFcGroup(state, out var leaveReason);
        using (ImRaii.Disabled(!leaveAllowed))
        {
            if (ImGui.Button("Leave Group##fcLeaveGroup"))
                TryLeaveFcGroup(mesh!);
        }
        if (!leaveAllowed && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(leaveReason);
    }

    private void TryCreateFcGroup(FcMeshNativeCoordinator mesh)
    {
        var result = mesh.CreateGroup();
        _fcUiMessage = result.Succeeded
            ? null
            : "FC group creation was rejected. Try again when the mesh is available.";
    }

    private void TryJoinFcGroup(FcMeshNativeCoordinator mesh)
    {
        var result = mesh.JoinGroup(System.Text.Encoding.UTF8.GetBytes(_fcJoinTicket));
        if (result.Succeeded)
        {
            _fcJoinTicket = string.Empty;
            _fcUiMessage = null;
        }
        else
        {
            _fcUiMessage = "Joining the FC group was rejected. Check the invite and try again.";
        }
    }

    private void TryLeaveFcGroup(FcMeshNativeCoordinator mesh)
    {
        var result = mesh.LeaveGroup();
        _fcUiMessage = result.Succeeded
            ? null
            : "Leaving the FC group was rejected. Finish the active FC work first.";
    }

    private bool CanLeaveFcGroup(FcMeshReadinessState state, out string reason)
    {
        reason = state is not FcMeshReadinessState.Ready
            ? "Connect to an FC group before leaving it."
            : string.Empty;
        if (state is not FcMeshReadinessState.Ready)
            return false;

        if (GatherBuddy.FcWorkerSessions?.Status.IsSubscribed == true)
        {
            reason = "Stop FC fulfillment before leaving the group.";
            return false;
        }

        if (GatherBuddy.FcFulfillment?.Diagnostics.State is { } fulfillmentState
            && fulfillmentState is not (FcFulfillmentControllerState.Idle or FcFulfillmentControllerState.Completed))
        {
            reason = "Stop FC fulfillment before leaving the group.";
            return false;
        }

        if (GatherBuddy.FcChestCoordinator?.HasPendingTransfer == true)
        {
            reason = "Finish the pending FC chest transfer before leaving the group.";
            return false;
        }

        if (HasPendingFcPublication())
        {
            reason = "Wait for FC updates to finish before leaving the group.";
            return false;
        }

        return true;
    }

    private static bool HasPendingFcPublication()
        => (GatherBuddy.FcPublishedLists?.Diagnostics.PendingCommands ?? 0) > 0
            || (GatherBuddy.FcChestPublication?.PendingCommands ?? 0) > 0
            || GatherBuddy.FcChestLocationPublication?.Diagnostics.HasPending == true;

    private void DrawFcLocalCopiesSection()
    {
        ImGui.TextColored(ImGuiColors.ParsedGold, "Your FC list copies");
        var service = GatherBuddy.FcPublishedLists;
        if (service is null)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey3, "FC list publishing is unavailable.");
            return;
        }

        var mappings = service.State.Lists ?? Array.Empty<FcLocalPublishedListState>();
        if (mappings.Length == 0)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey3, "No local FC list copies.");
            return;
        }

        var failed = new List<FcLocalPublishedListState>();
        var failedCount = mappings.Count(value => value.Pending?.Status == FcPublicationCommandStatus.Failed);
        foreach (var mapping in mappings)
        {
            var local = ResolveLocalList(mapping);
            var name = local?.Name
                ?? mapping.LastPublishedSnapshot?.DisplayName
                ?? "Unavailable list";
            var status = FormatLocalPublicationStatus(mapping, out var isFailed);
            ImGui.Text($"{name}  ·  {status}");
            if (isFailed)
                failed.Add(mapping);

            if (isFailed && failedCount == 1)
            {
                if (ImGui.SmallButton($"Retry failed publication##fcRetryLocal_{mapping.LocalListId}_{mapping.CreatedAtUtc.Ticks}"))
                    TryQueueFcPublication(service.RetryPending());
            }
        }

        if (failed.Count > 1)
        {
            if (ImGui.Button("Retry failed publications##fcRetryAllLocal"))
                TryQueueFcPublication(service.RetryPending());
        }
    }

    private static CraftingListDefinition? ResolveLocalList(FcLocalPublishedListState mapping)
    {
        var list = GatherBuddy.CraftingListManager.GetListByID(mapping.LocalListId);
        return list is not null
            && list.CreatedAt.ToUniversalTime() == mapping.CreatedAtUtc
            ? list
            : null;
    }

    private static string FormatLocalPublicationStatus(
        FcLocalPublishedListState mapping,
        out bool failed)
    {
        failed = mapping.Pending?.Status == FcPublicationCommandStatus.Failed;
        if (failed)
            return "Publication failed";
        if (mapping.Pending is not null)
            return "Publishing";
        if (mapping.LastPublishedSnapshot is { Published: true })
            return "Published";
        return "No longer published";
    }

    private List<PublishedListRecord> DrawFcPublicListsSection()
    {
        ImGui.TextColored(ImGuiColors.ParsedGold, "Public FC lists");
        var service = GatherBuddy.FcPublishedLists;
        if (service is null)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey3, "FC public lists are unavailable.");
            return new List<PublishedListRecord>();
        }

        var fulfillmentBusy = IsFcFulfillmentBusy();
        var compatibleIds = new HashSet<Guid>();
        var compatible = new List<PublishedListRecord>();
        var views = service.PublicLists;
        if (views.Count == 0)
            ImGui.TextColored(ImGuiColors.DalamudGrey3, "No public FC lists are available.");

        foreach (var view in views)
        {
            var record = view.Record;
            var name = string.IsNullOrWhiteSpace(record.DisplayName)
                ? "Unnamed FC list"
                : record.DisplayName;
            var owner = service.IsLocalOwner(record) ? "Yours" : "FC member";

            if (!record.Published)
            {
                ImGui.Text($"{name}  ·  {owner}  ·  No longer published");
            }
            else if (!view.IsCompatible)
            {
                ImGui.Text($"{name}  ·  {owner}  ·  Not compatible");
                if (view.CompatibilityReason is { Length: > 0 } compatibilityReason)
                    ImGui.TextWrapped(compatibilityReason);
            }
            else
            {
                compatibleIds.Add(record.ListId);
                compatible.Add(record);
                var selected = _fcSelectedPublicLists.Contains(record.ListId);
                using (ImRaii.Disabled(fulfillmentBusy))
                {
                    if (ImGui.Checkbox($"##fcSelect_{record.ListId:D}", ref selected))
                    {
                        if (selected)
                            _fcSelectedPublicLists.Add(record.ListId);
                        else
                            _fcSelectedPublicLists.Remove(record.ListId);
                    }
                }
                if (fulfillmentBusy && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip("Stop FC fulfillment before changing list selection.");
                ImGui.SameLine();
                ImGui.Text($"{name}  ·  {owner}");
            }

            foreach (var target in record.FinalTargets ?? Array.Empty<PublishedRecipeTarget>())
                ImGui.BulletText(FormatFcTarget(target));

            if (view.IsOrphanedLocalMapping && service.IsLocalOwner(record))
            {
                if (ImGui.Button($"Unpublish retained copy##fcOrphan_{record.ListId:D}"))
                    TryQueueFcPublication(service.Unpublish(record.ListId));
            }
        }

        _fcSelectedPublicLists.RemoveWhere(id => !compatibleIds.Contains(id));
        return compatible;
    }

    private static string FormatFcTarget(PublishedRecipeTarget target)
    {
        var recipe = RecipeManager.GetRecipe(target.RecipeId);
        var itemName = recipe is { } resolvedRecipe
            ? resolvedRecipe.ItemResult.Value.Name.ExtractText()
            : null;
        if (string.IsNullOrWhiteSpace(itemName)
            && Dalamud.GameData.GetExcelSheet<Item>() is { } itemSheet
            && itemSheet.TryGetRow(target.ItemId, out var item))
            itemName = item.Name.ExtractText();
        return string.IsNullOrWhiteSpace(itemName)
            ? $"Recipe {target.RecipeId} x{target.Quantity}"
            : $"{itemName} x{target.Quantity}";
    }

    private void DrawFcFulfillmentSection(IReadOnlyCollection<PublishedListRecord> compatible)
    {
        ImGui.TextColored(ImGuiColors.ParsedGold, "FC fulfillment");
        var controller = GatherBuddy.FcFulfillment;
        var readinessReady = GatherBuddy.FcMeshNative?.Readiness.IsReady == true;
        var busy = controller is not null
            && controller.Diagnostics.State is not (FcFulfillmentControllerState.Idle or FcFulfillmentControllerState.Completed);

        if (controller is null)
        {
            ImGui.TextColored(ImGuiColors.DalamudGrey3, "FC fulfillment is unavailable.");
            return;
        }

        var diagnostics = controller.Diagnostics;
        ImGui.Text($"Status: {FormatFcFulfillmentState(diagnostics.State)}");
        if (diagnostics.LastError is { Length: > 0 })
            ImGui.TextWrapped("FC fulfillment needs attention. Check the FC group and try again.");

        using (ImRaii.Disabled(busy))
            ImGui.Checkbox("Use own stock##fcUseOwnStock", ref _fcUseOwnStock);

        var canStart = !busy && readinessReady && compatible.Count > 0;
        var canStartSelected = canStart && _fcSelectedPublicLists.Count > 0;
        using (ImRaii.Disabled(!canStartSelected))
        {
            if (ImGui.Button("Fulfill Selected##fcFulfillSelected"))
            {
                if (!GatherBuddy.StartFcFulfillment(_fcSelectedPublicLists, _fcUseOwnStock, false))
                    _fcUiMessage = "Selected FC fulfillment could not be started. Check the FC group status.";
            }
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(!canStart))
        {
            if (ImGui.Button("Fulfill All##fcFulfillAll"))
            {
                if (!GatherBuddy.StartFcFulfillment(Array.Empty<Guid>(), _fcUseOwnStock, true))
                    _fcUiMessage = "FC fulfillment could not be started. Check the FC group status.";
            }
        }
        ImGui.SameLine();
        using (ImRaii.Disabled(!busy))
        {
            if (ImGui.Button("Stop Fulfillment##fcStopFulfillment"))
                GatherBuddy.StopFcFulfillment();
        }

        if (!readinessReady && !busy)
            ImGui.TextColored(ImGuiColors.DalamudGrey3, "Connect to an FC group before fulfilling lists.");
    }

    private void DrawFcChestSection()
    {
        ImGui.TextColored(ImGuiColors.ParsedGold, "FC chest");
        var locationService = GatherBuddy.FcChestLocationPublication;
        var location = locationService?.Diagnostics;
        ImGui.Text($"Saved FC chest: {(location?.HasPublishedLocation == true ? "Set" : "Not set")}");

        using (ImRaii.Disabled(locationService is null))
        {
            if (ImGui.Button("Set currently open chest##fcSetChest"))
            {
                if (!GatherBuddy.QueueRegisterCurrentFcChestLocation())
                    _fcUiMessage = "FC chest registration needs attention. Check the current chest and try again.";
                else
                    _fcUiMessage = null;
            }
        }
        if (locationService is null && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("FC chest registration is unavailable.");
        ImGui.SameLine();
        var canRemoveChest = location is { HasPublishedLocation: true, HasPending: false };
        using (ImRaii.Disabled(!canRemoveChest))
        {
            if (ImGui.Button("Remove saved chest##fcRemoveChest"))
                TryRemoveSavedChest();
        }
        if (location is { LastError: { Length: > 0 } })
            ImGui.TextWrapped("FC chest registration needs attention. Check the current chest and try again.");

        var coordinator = GatherBuddy.FcChestCoordinator;
        var chestDiagnostics = coordinator?.Diagnostics;
        ImGui.SameLine();
        var canCheckChest = coordinator is not null && !coordinator.HasPendingTransfer;
        using (ImRaii.Disabled(!canCheckChest))
        {
            if (ImGui.Button("Check FC Chest##fcCheckChest"))
                coordinator!.RequestCheck();
        }
        if (!canCheckChest && ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(coordinator is null
                ? "FC chest checking is unavailable."
                : "Finish the pending chest transfer before checking the chest.");

        if (chestDiagnostics is { State: not FcChestCoordinatorState.Idle })
            ImGui.Text($"Chest: {FormatFcChestState(chestDiagnostics.State)}");
        if (chestDiagnostics is { LastError: { Length: > 0 } }
            && (chestDiagnostics.State == FcChestCoordinatorState.Blocked
                || chestDiagnostics.State == FcChestCoordinatorState.CleanupPending))
            ImGui.TextWrapped("FC chest checking needs attention. Check the chest and try again.");

        ImGui.Spacing();
        ImGui.TextColored(ImGuiColors.DalamudGrey3, "Public chest destinations");
        var navigation = GatherBuddy.FcPublicChestNavigation;
        foreach (var destination in FcChestLocationCatalog.All)
        {
            if (navigation is not null)
            {
                if (ImGui.SmallButton($"Route##fcChestRoute_{destination.Key}"))
                {
                    if (!GatherBuddy.QueueFcPublicChestRoute(destination))
                        _fcUiMessage = "Public FC chest travel could not be started.";
                    else
                        _fcUiMessage = null;
                }
                ImGui.SameLine();
            }
            ImGui.Text($"{destination.Zone} / {destination.AethernetShard}");
        }

        if (navigation is { State: FcPublicChestNavigationState.Traveling or FcPublicChestNavigationState.Navigating })
        {
            ImGui.Text($"Traveling to {navigation.Destination?.Zone ?? "the FC chest"}");
            if (ImGui.SmallButton("Stop travel##fcStopChestTravel"))
                GatherBuddy.QueueStopFcPublicChestRoute();
        }
        if (navigation is { LastError: { Length: > 0 } }
            && navigation.State == FcPublicChestNavigationState.Blocked)
            ImGui.TextWrapped("Public FC chest travel needs attention. Try the route again.");
        if (navigation is null)
            ImGui.TextColored(ImGuiColors.DalamudGrey3, "Public chest travel is unavailable.");
    }

    private void TryRemoveSavedChest()
    {
        var result = GatherBuddy.UnregisterCurrentFcChestLocation();
        _fcUiMessage = result.Accepted
            ? null
            : "FC chest registration needs attention. Check the current chest and try again.";
    }

    private void DrawFcListContextActions(CraftingListDefinition list, string listUiId)
    {
        if (!GatherBuddy.DevelopmentFeatures.Allows(DevelopmentFeature.FcUserInterface))
            return;

        ImGui.Separator();
        var service = GatherBuddy.FcPublishedLists;
        var ready = service is not null
            && service.Diagnostics.WritesAllowed
            && GatherBuddy.FcMeshNative?.Readiness.IsReady == true;
        if (!ready)
        {
            using (ImRaii.Disabled(true))
                ImGui.Selectable($"FC publishing unavailable##fcUnavailable_{listUiId}");
            if (ImGui.Selectable($"Open FC Lists##fcOpenLists_{listUiId}"))
                OpenToFcLists();
            return;
        }

        var state = service!.State.Lists.FirstOrDefault(value => value.LocalListId == list.ID
            && value.CreatedAtUtc == list.CreatedAt.ToUniversalTime());
        if (state?.Pending is { Status: not FcPublicationCommandStatus.Failed })
        {
            using (ImRaii.Disabled(true))
                ImGui.Selectable($"FC publication in progress##fcInProgress_{listUiId}");
            return;
        }

        if (state?.Pending?.Status == FcPublicationCommandStatus.Failed)
        {
            using (ImRaii.Disabled(true))
                ImGui.Selectable($"FC publication failed##fcFailed_{listUiId}");
            if (ImGui.Selectable($"Open FC Lists##fcOpenFailed_{listUiId}"))
                OpenToFcLists();
            return;
        }

        if (state?.LastPublishedSnapshot is not { Published: true })
        {
            if (ImGui.Selectable($"Publish to FC##fcPublish_{listUiId}"))
                TryQueueFcPublication(service.Publish(list));
            return;
        }

        if (ImGui.Selectable($"Update FC Copy##fcUpdate_{listUiId}"))
            TryQueueFcPublication(service.Update(list));
        if (ImGui.Selectable($"Unpublish FC Copy##fcUnpublish_{listUiId}"))
            TryQueueFcPublication(service.Unpublish(list));
    }

    private void TryQueueFcPublication(FcPublicationResult result)
    {
        if (result.Accepted)
        {
            _fcUiMessage = null;
            return;
        }

        _fcUiMessage = result.Message;
        GatherBuddy.Log.Warning($"FC list publication rejected: {result.Message}");
    }

    private static bool IsFcFulfillmentBusy()
        => GatherBuddy.FcFulfillment?.Diagnostics.State is { } state
            && state is not (FcFulfillmentControllerState.Idle or FcFulfillmentControllerState.Completed);

    private static string FormatFcReadinessState(FcMeshReadinessState state)
        => state switch
        {
            FcMeshReadinessState.NotCreated
                or FcMeshReadinessState.Created
                or FcMeshReadinessState.Left => "Not set up",
            FcMeshReadinessState.Joining
                or FcMeshReadinessState.ReconcilingSnapshot => "Connecting",
            FcMeshReadinessState.Ready => "Ready",
            FcMeshReadinessState.Error => "Needs attention",
            _ => "Needs attention",
        };

    private static string FormatFcFulfillmentState(FcFulfillmentControllerState state)
        => state switch
        {
            FcFulfillmentControllerState.Idle => "Idle",
            FcFulfillmentControllerState.Completed => "Completed",
            FcFulfillmentControllerState.StartingSession
                or FcFulfillmentControllerState.Recovering
                or FcFulfillmentControllerState.DeriveWorld
                or FcFulfillmentControllerState.Planning
                or FcFulfillmentControllerState.BuildExecutionPlan => "Preparing",
            FcFulfillmentControllerState.EnsureChestKnown
                or FcFulfillmentControllerState.CheckingChest => "Checking FC chest",
            FcFulfillmentControllerState.Withdrawing => "Withdrawing materials",
            FcFulfillmentControllerState.Gathering => "Gathering",
            FcFulfillmentControllerState.Crafting => "Crafting",
            FcFulfillmentControllerState.Depositing => "Depositing results",
            FcFulfillmentControllerState.WaitingConnectivity => "Waiting for connection",
            FcFulfillmentControllerState.WaitingRemote => "Waiting for materials",
            FcFulfillmentControllerState.AwaitingCapability => "Waiting for HQ crafter",
            FcFulfillmentControllerState.Stopping
                or FcFulfillmentControllerState.CleanupPending => "Stopping",
            FcFulfillmentControllerState.Blocked => "Needs attention",
            _ => "Needs attention",
        };

    private static string FormatFcChestState(FcChestCoordinatorState state)
        => state switch
        {
            FcChestCoordinatorState.Idle => "Ready",
            FcChestCoordinatorState.CheckingChest => "Checking FC chest",
            FcChestCoordinatorState.Ready => "Ready",
            FcChestCoordinatorState.Withdrawing => "Withdrawing materials",
            FcChestCoordinatorState.Depositing => "Depositing results",
            FcChestCoordinatorState.Reconciling => "Reconciling chest",
            FcChestCoordinatorState.CleanupPending => "Needs attention",
            _ => "Needs attention",
        };
}
