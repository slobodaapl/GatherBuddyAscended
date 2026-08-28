using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using ElliLib;
using ElliLib.Raii;
using GatherBuddy.Crafting;
using GatherBuddy.Crafting.Acquisition;
using GatherBuddy.Marketboard;
using GatherBuddy.Plugin;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Gui;

public sealed class CraftingPurchaseConfigurationWindow : Window, IDisposable
{
    private static readonly TimeSpan EstimateTimeout = TimeSpan.FromSeconds(10);
    private static readonly Vector4 AvailableColor = new(0.35f, 0.9f, 0.45f, 1f);
    private static readonly Vector4 UnavailableColor = new(1f, 0.35f, 0.35f, 1f);
    private static readonly Vector4 UnknownColor = new(0.75f, 0.75f, 0.75f, 1f);
    private static float CompactPadding => VulcanUiScaling.Scaled(2f);
    private static float PurchaseRowHeight => VulcanUiScaling.Scaled(32f);

    private CraftingListEditor? _editor;
    private AcquisitionPlanningInputBuilder.BuildResult? _configuration;
    private IReadOnlyDictionary<uint, MarketAvailability> _marketAvailability
        = new Dictionary<uint, MarketAvailability>();
    private CancellationTokenSource? _availabilityCancellation;
    private Task<IReadOnlyDictionary<uint, MarketAvailability>>? _availabilityTask;
    private CancellationTokenSource? _estimateCancellation;
    private Task<CraftingAcquisitionService.Evaluation>? _estimateTask;
    private CraftingAcquisitionService.Evaluation? _estimate;
    private string _status = string.Empty;
    private bool? _pendingCollapseState;
    private bool _requestFocus;
    private DateTime _nextVendorRefresh;

    private readonly record struct CurrencyChoice(
        IReadOnlyList<uint>? CurrencyIds,
        string Initials,
        string FullName,
        IReadOnlyList<uint> IconIds);

    internal sealed record PurchaseTableContext(
        AcquisitionPlanningInputBuilder.BuildResult? Configuration,
        IReadOnlyDictionary<uint, MarketAvailability> MarketAvailability,
        AcquisitionPlan? Plan,
        Func<uint, AcquisitionItemPurchasePolicy?> ReadPolicy,
        Action<uint, AcquisitionItemPurchasePolicy> WritePolicy,
        string TableId);

    public CraftingPurchaseConfigurationWindow()
        : base("Purchase configuration###CraftingPurchaseConfiguration")
    {
        IsOpen = false;
        Size = VulcanUiScaling.Scaled(900f, 520f);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = VulcanUiScaling.Scaled(680f, 320f),
            MaximumSize = VulcanUiScaling.Scaled(1600f, 1400f),
        };
    }

    public void SetEditor(CraftingListEditor? editor)
    {
        if (ReferenceEquals(_editor, editor))
            return;
        CancelAvailability();
        InvalidateEstimate();
        _editor = editor;
        _configuration = null;
        _marketAvailability = new Dictionary<uint, MarketAvailability>();
        _status = string.Empty;
        if (editor == null)
            IsOpen = false;
    }

    public void Probe(CraftingListEditor editor)
    {
        SetEditor(editor);
        RefreshConfiguration();
        StartAvailabilityProbe();
    }

    public void Disable(CraftingListEditor editor)
    {
        if (!ReferenceEquals(_editor, editor))
            return;
        CancelAvailability();
        InvalidateEstimate();
        IsOpen = false;
        GatherBuddy.CraftingPurchasePlanWindow?.Invalidate(editor);
    }

    public void Invalidate(CraftingListEditor editor)
    {
        if (!ReferenceEquals(_editor, editor)
            || !editor.PurchaseConfigurationList.AutoPurchaseBlockedDependencies)
            return;
        Probe(editor);
    }

    public void OpenOrRestore(CraftingListEditor editor)
    {
        Probe(editor);
        IsOpen = true;
        _pendingCollapseState = false;
        _requestFocus = true;
    }

    public override bool DrawConditions()
        => _editor != null && IsOpen;

    public override void PreDraw()
    {
        if (_editor != null)
            WindowName = $"Purchase configuration: {_editor.ListName}###CraftingPurchaseConfiguration";
        if (_pendingCollapseState.HasValue)
        {
            ImGui.SetNextWindowCollapsed(_pendingCollapseState.Value, ImGuiCond.Always);
            _pendingCollapseState = null;
        }
        if (_requestFocus)
        {
            ImGui.SetNextWindowFocus();
            _requestFocus = false;
        }
    }

    public override void Draw()
    {
        if (_editor == null)
            return;

        ConsumeAvailability();
        ConsumeEstimate();
        if (_configuration?.IsLoading == true && DateTime.UtcNow >= _nextVendorRefresh)
        {
            RefreshConfiguration();
            _nextVendorRefresh = DateTime.UtcNow.AddMilliseconds(500);
        }

        using var theme = VulcanUiStyle.PushTheme();
        var footerHeight = ImGui.GetFrameHeightWithSpacing() + 1f;
        ImGui.BeginChild("##purchaseConfigurationRows", new Vector2(0, -footerHeight), false);
        DrawRows();
        ImGui.EndChild();
        ImGui.Separator();
        DrawFooter();
    }

    private void DrawRows()
        => DrawPurchaseTable(new PurchaseTableContext(
            _configuration,
            _marketAvailability,
            _estimate?.Planning?.SelectedPlan,
            itemId => _editor!.PurchaseConfigurationList.PurchaseItemPolicies.GetValueOrDefault(itemId),
            SetPolicy,
            "##purchaseConfigurationTable"),
            ImGui.GetContentRegionAvail().Y);

    internal static void DrawPurchaseTable(PurchaseTableContext context, float height)
    {
        if (context.Configuration == null)
        {
            ImGui.TextDisabled("Purchase sources are not loaded.");
            return;
        }

        var rows = GetBlockedDependencies(context.Configuration.Input.Dependencies).ToArray();
        if (rows.Length == 0)
        {
            ImGui.TextDisabled("No uncraftable or ungatherable dependencies.");
            return;
        }

        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV
            | ImGuiTableFlags.ScrollY | ImGuiTableFlags.SizingStretchProp;
        using var cellPadding = ImRaii.PushStyle(ImGuiStyleVar.CellPadding, new Vector2(CompactPadding));
        if (!ImGui.BeginTable(context.TableId, 6, flags, new Vector2(0, height)))
            return;

        var statusWidth = ImGui.GetTextLineHeight() + CompactPadding * 2f;
        var sourceWidth = ImGui.CalcTextSize("Marketplace").X + ImGui.GetFrameHeight() + ImGui.GetStyle().FramePadding.X * 2f;
        var currencyWidth = VulcanUiScaling.Scaled(118f);
        var hqWidth = ImGui.GetFrameHeight() + CompactPadding * 2f;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("##vendor", ImGuiTableColumnFlags.WidthFixed, statusWidth);
        ImGui.TableSetupColumn("##market", ImGuiTableColumnFlags.WidthFixed, statusWidth);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 1f);
        ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthFixed, sourceWidth);
        ImGui.TableSetupColumn("Currency", ImGuiTableColumnFlags.WidthFixed, currencyWidth);
        ImGui.TableSetupColumn("##preferHq", ImGuiTableColumnFlags.WidthFixed, hqWidth);
        DrawHeaders();

        var itemCosts = context.Plan is { } plan
            ? CraftingPurchasePlanWindow.BuildItemEstimates(plan).ToDictionary(row => row.ItemId, row => row.Costs)
            : new Dictionary<uint, IReadOnlyList<AcquisitionCurrencyCost>>();
        foreach (var dependency in rows)
        {
            ImGui.PushID((int)dependency.ItemId);
            var offers = GetOffers(context.Configuration, dependency.ItemId);
            var market = context.MarketAvailability.GetValueOrDefault(dependency.ItemId,
                new MarketAvailability(dependency.ItemId, MarketAvailabilityState.Unknown, "Market availability has not been checked."));
            var vendorAvailable = offers.Any(offer => offer.IsAvailable);
            var marketAvailable = market.State == MarketAvailabilityState.Available;
            var marketKnown = market.State != MarketAvailabilityState.Unknown;
            var policy = GetPolicy(context, dependency.ItemId, offers, vendorAvailable, marketAvailable, marketKnown);

            ImGui.TableNextRow(ImGuiTableRowFlags.None, PurchaseRowHeight);
            ImGui.TableSetColumnIndex(0);
            DrawVendorStatus(context.Configuration, offers);
            ImGui.TableSetColumnIndex(1);
            DrawMarketStatus(market);
            ImGui.TableSetColumnIndex(3);
            var itemRightEdge = ImGui.GetCursorPosX() - CompactPadding * 2f - ImGui.GetStyle().ItemSpacing.X;
            ImGui.TableSetColumnIndex(2);
            DrawItem(
                dependency,
                itemCosts.GetValueOrDefault(dependency.ItemId, Array.Empty<AcquisitionCurrencyCost>()),
                itemRightEdge);
            ImGui.TableSetColumnIndex(3);
            policy = DrawSource(context, dependency.ItemId, policy, offers, vendorAvailable, marketAvailable, marketKnown);
            ImGui.TableSetColumnIndex(4);
            policy = DrawCurrency(context, dependency.ItemId, policy, offers, marketAvailable);
            ImGui.TableSetColumnIndex(5);
            DrawPreferHq(context, dependency.ItemId, policy);
            ImGui.PopID();
        }
        ImGui.EndTable();
    }

    private static void DrawHeaders()
    {
        ImGui.TableNextRow(ImGuiTableRowFlags.Headers);
        ImGui.TableSetColumnIndex(0);
        DrawCenteredHeader("\uf51e", iconFont: true);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Vendor");
        ImGui.TableSetColumnIndex(1);
        DrawCenteredHeader("\uf290", iconFont: true);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Market");
        ImGui.TableSetColumnIndex(2);
        ImGui.TextUnformatted("Item");
        ImGui.TableSetColumnIndex(3);
        ImGui.TextUnformatted("Source");
        ImGui.TableSetColumnIndex(4);
        ImGui.TextUnformatted("Currency");
        ImGui.TableSetColumnIndex(5);
        DrawCenteredHeader("HQ", iconFont: false);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Prefer HQ");
    }

    private static void DrawCenteredHeader(string text, bool iconFont)
    {
        using var font = ImRaii.PushFont(UiBuilder.IconFont, iconFont);
        var width = ImGui.CalcTextSize(text).X;
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0f, (ImGui.GetContentRegionAvail().X - width) / 2f));
        ImGui.TextUnformatted(text);
    }

    private static IReadOnlyList<AcquisitionVendorOffer> GetOffers(
        AcquisitionPlanningInputBuilder.BuildResult configuration,
        uint itemId)
        => configuration.Input.VendorOffers
            .Where(offer => offer.EffectiveOutputs.Any(output => output.ItemId == itemId && output.Quantity > 0))
            .ToArray();

    private static void DrawVendorStatus(
        AcquisitionPlanningInputBuilder.BuildResult configuration,
        IReadOnlyList<AcquisitionVendorOffer> offers)
    {
        if (offers.Any(offer => offer.IsAvailable))
        {
            var details = offers.Where(offer => offer.IsAvailable)
                .Select(offer => $"{offer.VendorName}: {FormatCosts(offer.Costs)}")
                .Distinct();
            DrawStatusIcon(FontAwesomeIcon.Check, AvailableColor, string.Join("\n", details));
            return;
        }
        if (offers.Count != 0)
        {
            DrawStatusIcon(FontAwesomeIcon.Times, UnavailableColor,
                string.Join("\n", offers.Select(offer => offer.UnavailableReason).Where(reason => !string.IsNullOrWhiteSpace(reason)).Distinct()));
            return;
        }
        if (configuration.IsLoading)
            DrawStatusIcon(FontAwesomeIcon.QuestionCircle, UnknownColor, configuration.LoadingReason);
        else
            DrawStatusIcon(FontAwesomeIcon.Ban, UnavailableColor, "No vendor source exists.");
    }

    private static void DrawMarketStatus(MarketAvailability availability)
    {
        var (icon, color) = availability.State switch
        {
            MarketAvailabilityState.Available => (FontAwesomeIcon.Check, AvailableColor),
            MarketAvailabilityState.Unavailable => (FontAwesomeIcon.Times, UnavailableColor),
            _ => (FontAwesomeIcon.QuestionCircle, UnknownColor),
        };
        DrawStatusIcon(icon, color, availability.Reason);
    }

    private static void DrawStatusIcon(FontAwesomeIcon icon, Vector4 color, string tooltip)
    {
        CenterCellItem(ImGui.GetTextLineHeight());
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            var iconText = icon.ToIconString();
            var width = ImGui.CalcTextSize(iconText).X;
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0f, (ImGui.GetContentRegionAvail().X - width) / 2f));
            ImGui.TextColored(color, icon.ToIconString());
        }
        if (ImGui.IsItemHovered() && !string.IsNullOrWhiteSpace(tooltip))
            ImGui.SetTooltip(tooltip);
    }

    private static void DrawItem(
        AcquisitionDependency dependency,
        IReadOnlyList<AcquisitionCurrencyCost> costs,
        float rightEdge)
    {
        var rowTop = ImGui.GetCursorPosY();
        var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
        var iconId = itemSheet?.TryGetRow(dependency.ItemId, out var item) == true ? (uint)item.Icon : 0u;
        var size = VulcanUiScaling.Scaled(28f, 28f);
        if (iconId != 0 && Icons.DefaultStorage.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId)).TryGetWrap(out var wrap, out _))
            ImGui.Image(wrap.Handle, size);
        else
            ImGui.Dummy(size);
        ImGui.SameLine();
        ImGui.SetCursorPosY(ImGui.GetCursorPosY()
            + Math.Max(0f, (size.Y - ImGui.GetTextLineHeight()) / 2f));
        ImGui.TextUnformatted($"{dependency.ItemName}  x{dependency.RequiredQuantity:N0}");
        var classJobId = dependency.SelectedPath?.JobId ?? CraftingRowIcons.GetMaterialClassJobId(dependency.ItemId, false);
        if (classJobId != 0)
        {
            ImGui.SameLine();
            CraftingRowIcons.DrawIconsRightAligned(new[] { CraftingRowIcons.GetClassJobIcon(classJobId) }, size.X);
        }
        CraftingPurchasePlanWindow.DrawCostsRightAligned(costs, rightEdge, rowTop, size.Y);
    }

    private static AcquisitionItemPurchasePolicy DrawSource(
        PurchaseTableContext context,
        uint itemId,
        AcquisitionItemPurchasePolicy policy,
        IReadOnlyList<AcquisitionVendorOffer> offers,
        bool vendorAvailable,
        bool marketAvailable,
        bool marketKnown)
    {
        var choices = new List<AcquisitionSourceSelection>();
        if (vendorAvailable)
            choices.Add(AcquisitionSourceSelection.Vendor);
        if (marketAvailable)
            choices.Add(AcquisitionSourceSelection.Marketplace);
        if (vendorAvailable && (marketAvailable || !marketKnown))
            choices.Add(AcquisitionSourceSelection.Either);
        if (!marketKnown && policy.UserConfigured && !choices.Contains(policy.Source))
        {
            ImGui.TextDisabled($"{SourceLabel(policy.Source)} (checking)");
            return policy;
        }
        if (choices.Count == 0)
        {
            ImGui.TextDisabled(marketKnown ? "Unavailable" : "Checking...");
            return policy;
        }

        if (!choices.Contains(policy.Source))
        {
            policy = CreatePolicy(choices.Count == 1 ? choices[0] : AcquisitionSourceSelection.Either, offers, policy.PreferHQ);
            context.WritePolicy(itemId, policy);
        }

        using var disabled = ImRaii.Disabled(choices.Count == 1);
        CenterCellItem(ImGui.GetFrameHeight());
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.BeginCombo("##source", SourceLabel(policy.Source)))
        {
            foreach (var choice in choices.OrderBy(choice => choice == AcquisitionSourceSelection.Either ? 0 : 1))
            {
                if (ImGui.Selectable(SourceLabel(choice), choice == policy.Source))
                {
                    policy = MarkUserConfigured(CreatePolicy(choice, offers, policy.PreferHQ));
                    context.WritePolicy(itemId, policy);
                }
            }
            ImGui.EndCombo();
        }
        return policy;
    }

    private static AcquisitionItemPurchasePolicy DrawCurrency(
        PurchaseTableContext context,
        uint itemId,
        AcquisitionItemPurchasePolicy policy,
        IReadOnlyList<AcquisitionVendorOffer> offers,
        bool marketAvailable)
    {
        var choices = BuildCurrencyChoices(policy.Source, offers, marketAvailable);
        if (choices.Count == 0)
        {
            ImGui.TextDisabled("Unavailable");
            return policy;
        }
        var selected = choices.FirstOrDefault(choice => CurrencyEquals(choice.CurrencyIds, policy.CurrencyIds));
        if (string.IsNullOrEmpty(selected.FullName))
        {
            selected = choices[0];
            policy = CopyPolicy(policy, selected.CurrencyIds, preferHq: false);
            context.WritePolicy(itemId, policy);
        }

        using var disabled = ImRaii.Disabled(choices.Count == 1);
        CenterCellItem(ImGui.GetFrameHeight());
        ImGui.SetNextItemWidth(-1f);
        var previewDrawList = ImGui.GetWindowDrawList();
        var preview = CurrencyPreviewText(selected);
        var comboOpen = ImGui.BeginCombo("##currency", preview);
        var previewMin = ImGui.GetItemRectMin();
        var previewMax = ImGui.GetItemRectMax();
        var previewHovered = ImGui.IsItemHovered();
        if (comboOpen)
        {
            for (var i = 0; i < choices.Count; i++)
            {
                var choice = choices[i];
                var selectedChoice = CurrencyEquals(choice.CurrencyIds, policy.CurrencyIds);
                var optionDrawList = ImGui.GetWindowDrawList();
                var clicked = ImGui.Selectable(
                    $"{CurrencyPreviewText(choice)}##currencyChoice{i}",
                    selectedChoice,
                    ImGuiSelectableFlags.None,
                    new Vector2(0f, ImGui.GetFrameHeight()));
                var optionMin = ImGui.GetItemRectMin();
                var optionMax = ImGui.GetItemRectMax();
                DrawCurrencyIcons(optionDrawList, optionMin, optionMax, choice.IconIds);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(choice.FullName);
                if (clicked)
                {
                    var allowHq = AllowsPreferHq(policy.Source, choice.CurrencyIds, ItemCanBeHq(itemId));
                    policy = MarkUserConfigured(CopyPolicy(policy, choice.CurrencyIds, allowHq && policy.PreferHQ));
                    context.WritePolicy(itemId, policy);
                }
            }
            ImGui.EndCombo();
        }
        DrawCurrencyIcons(previewDrawList, previewMin, previewMax, selected.IconIds);
        if (previewHovered)
            ImGui.SetTooltip(selected.FullName);
        return policy;
    }

    private static string CurrencyPreviewText(CurrencyChoice choice)
        => choice.IconIds.Count == 0
            ? choice.Initials
            : $"{new string(' ', choice.IconIds.Count * 4)}{choice.Initials}";

    private static void DrawCurrencyIcons(
        ImDrawListPtr drawList,
        Vector2 itemMin,
        Vector2 itemMax,
        IReadOnlyList<uint> iconIds)
    {
        if (iconIds.Count == 0)
            return;
        var iconSize = Math.Min(VulcanUiScaling.Scaled(18f), itemMax.Y - itemMin.Y - CompactPadding * 2f);
        var x = itemMin.X + ImGui.GetStyle().FramePadding.X;
        var y = itemMin.Y + (itemMax.Y - itemMin.Y - iconSize) / 2f;
        foreach (var iconId in iconIds)
        {
            var texture = Icons.DefaultStorage.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId));
            if (texture.TryGetWrap(out var wrap, out _))
                drawList.AddImage(wrap.Handle, new Vector2(x, y), new Vector2(x + iconSize, y + iconSize));
            x += iconSize + CompactPadding;
        }
    }

    private static void DrawPreferHq(
        PurchaseTableContext context,
        uint itemId,
        AcquisitionItemPurchasePolicy policy)
    {
        var enabled = AllowsPreferHq(policy.Source, policy.CurrencyIds, ItemCanBeHq(itemId));
        if (!enabled && policy.PreferHQ)
        {
            policy = CopyPolicy(policy, policy.CurrencyIds, preferHq: false);
            context.WritePolicy(itemId, policy);
        }
        var preferHq = enabled && policy.PreferHQ;
        using var disabled = ImRaii.Disabled(!enabled);
        CenterCellItem(ImGui.GetFrameHeight());
        var checkboxWidth = ImGui.GetFrameHeight();
        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + Math.Max(0f, (ImGui.GetContentRegionAvail().X - checkboxWidth) / 2f));
        if (ImGui.Checkbox("##preferHq", ref preferHq))
            context.WritePolicy(itemId, MarkUserConfigured(CopyPolicy(policy, policy.CurrencyIds, preferHq)));
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && !enabled)
            ImGui.SetTooltip("HQ preference requires marketplace access and Auto or Gil currency.");
    }

    private static void CenterCellItem(float itemHeight)
        => ImGui.SetCursorPosY(ImGui.GetCursorPosY()
            + Math.Max(0f, (PurchaseRowHeight - itemHeight) / 2f - CompactPadding));

    private static AcquisitionItemPurchasePolicy GetPolicy(
        PurchaseTableContext context,
        uint itemId,
        IReadOnlyList<AcquisitionVendorOffer> offers,
        bool vendorAvailable,
        bool marketAvailable,
        bool marketKnown)
    {
        var policy = context.ReadPolicy(itemId);
        if (policy?.UserConfigured == true)
            return policy;
        return CreateImplicitPolicy(offers, vendorAvailable, marketAvailable, marketKnown)
            ?? new AcquisitionItemPurchasePolicy();
    }

    internal static AcquisitionItemPurchasePolicy? CreateImplicitPolicy(
        IReadOnlyList<AcquisitionVendorOffer> offers,
        bool vendorAvailable,
        bool marketAvailable,
        bool marketKnown)
    {
        if (!marketKnown)
            return null;
        if (vendorAvailable && marketAvailable)
            return new AcquisitionItemPurchasePolicy();
        if (marketAvailable)
        {
            return new AcquisitionItemPurchasePolicy
            {
                Source = AcquisitionSourceSelection.Marketplace,
                CurrencyIds = new[] { AcquisitionCurrency.GilId },
            };
        }
        return vendorAvailable
            ? CreatePolicy(AcquisitionSourceSelection.Vendor, offers, false)
            : null;
    }

    private static AcquisitionItemPurchasePolicy CreatePolicy(
        AcquisitionSourceSelection source,
        IReadOnlyList<AcquisitionVendorOffer> offers,
        bool preferHq)
    {
        IReadOnlyList<uint>? currencies = source switch
        {
            AcquisitionSourceSelection.Either => null,
            AcquisitionSourceSelection.Marketplace => new[] { AcquisitionCurrency.GilId },
            _ => NormalizeCurrencyIds(offers.First(offer => offer.IsAvailable).Costs.Select(cost => cost.CurrencyId)),
        };
        return new AcquisitionItemPurchasePolicy
        {
            Source = source,
            CurrencyIds = currencies,
            PreferHQ = source != AcquisitionSourceSelection.Vendor && preferHq,
        };
    }

    private static AcquisitionItemPurchasePolicy CopyPolicy(
        AcquisitionItemPurchasePolicy policy,
        IReadOnlyList<uint>? currencies,
        bool preferHq)
        => new()
        {
            Source = policy.Source,
            CurrencyIds = currencies?.ToArray(),
            PreferHQ = preferHq,
            UserConfigured = policy.UserConfigured,
        };

    private static AcquisitionItemPurchasePolicy MarkUserConfigured(AcquisitionItemPurchasePolicy policy)
        => new()
        {
            Source = policy.Source,
            CurrencyIds = policy.CurrencyIds?.ToArray(),
            PreferHQ = policy.PreferHQ,
            UserConfigured = true,
        };

    private void SetPolicy(uint itemId, AcquisitionItemPurchasePolicy policy)
    {
        _editor!.PurchaseConfigurationList.PurchaseItemPolicies[itemId] = policy;
        _editor.SavePurchaseConfiguration();
        InvalidateEstimate();
    }

    private static List<CurrencyChoice> BuildCurrencyChoices(
        AcquisitionSourceSelection source,
        IReadOnlyList<AcquisitionVendorOffer> offers,
        bool marketAvailable)
    {
        if (source == AcquisitionSourceSelection.Marketplace)
            return new List<CurrencyChoice> { CreateGilChoice() };

        var choices = offers.Where(offer => offer.IsAvailable)
            .GroupBy(offer => string.Join(",", NormalizeCurrencyIds(offer.Costs.Select(cost => cost.CurrencyId))))
            .Select(group => CreateCurrencyChoice(group.First().Costs))
            .ToList();
        if (source == AcquisitionSourceSelection.Either)
        {
            choices.Insert(0, new CurrencyChoice(null, "Auto", "Auto", Array.Empty<uint>()));
            if (marketAvailable && choices.All(choice => !CurrencyEquals(choice.CurrencyIds, new[] { AcquisitionCurrency.GilId })))
                choices.Add(CreateGilChoice());
        }
        return choices;
    }

    private static CurrencyChoice CreateCurrencyChoice(IReadOnlyList<AcquisitionCurrencyCost> costs)
    {
        var names = costs.Select(cost => cost.CurrencyName).ToArray();
        return new CurrencyChoice(
            NormalizeCurrencyIds(costs.Select(cost => cost.CurrencyId)),
            string.Join("+", names.Select(CurrencyInitials)),
            string.Join(" + ", names),
            costs.Select(cost => cost.IconId != 0 ? cost.IconId : ResolveCurrencyIcon(cost.CurrencyId))
                .Where(iconId => iconId != 0)
                .ToArray());
    }

    private static CurrencyChoice CreateGilChoice()
        => new(
            new[] { AcquisitionCurrency.GilId },
            "G",
            "Gil",
            ResolveCurrencyIcon(AcquisitionCurrency.GilId) is var iconId && iconId != 0
                ? new[] { iconId }
                : Array.Empty<uint>());

    private static string CurrencyInitials(string name)
    {
        var words = name.Split([' ', '-', '_', '\'', '/'], StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0
            ? "?"
            : string.Concat(words.Take(4).Select(word => char.ToUpperInvariant(word[0])));
    }

    private static uint ResolveCurrencyIcon(uint currencyId)
    {
        var itemId = currencyId == AcquisitionCurrency.GilId ? 1u : currencyId;
        return Dalamud.GameData.GetExcelSheet<Item>()?.TryGetRow(itemId, out var item) == true
            ? (uint)item.Icon
            : 0u;
    }

    internal static bool AllowsPreferHq(
        AcquisitionSourceSelection source,
        IReadOnlyList<uint>? currencyIds,
        bool itemCanBeHq)
        => itemCanBeHq
            && source != AcquisitionSourceSelection.Vendor
            && (currencyIds == null || NormalizeCurrencyIds(currencyIds).SequenceEqual(new[] { AcquisitionCurrency.GilId }));

    internal static uint[] NormalizeCurrencyIds(IEnumerable<uint> currencyIds)
        => currencyIds.Distinct().OrderBy(currencyId => currencyId).ToArray();

    private static bool CurrencyEquals(IReadOnlyList<uint>? left, IReadOnlyList<uint>? right)
        => left == null || right == null
            ? left == null && right == null
            : NormalizeCurrencyIds(left).SequenceEqual(NormalizeCurrencyIds(right));

    private static string FormatCosts(IReadOnlyList<AcquisitionCurrencyCost> costs)
        => string.Join(" + ", costs.Select(cost => $"{cost.Amount:N0} {cost.CurrencyName}"));

    private static string SourceLabel(AcquisitionSourceSelection source)
        => source switch
        {
            AcquisitionSourceSelection.Vendor => "Vendor",
            AcquisitionSourceSelection.Marketplace => "Marketplace",
            _ => "Either",
        };

    private static bool ItemCanBeHq(uint itemId)
        => Dalamud.GameData.GetExcelSheet<Item>()?.TryGetRow(itemId, out var item) == true && item.CanBeHq;

    private void DrawFooter()
    {
        var list = _editor!.PurchaseConfigurationList;
        var maxGil = (int)Math.Clamp(list.MaximumGilSpend ?? 0, 0, int.MaxValue);
        ImGui.SetNextItemWidth(VulcanUiScaling.Scaled(150f));
        if (ImGui.InputInt("Max Gil##purchaseMaxGil", ref maxGil))
        {
            list.MaximumGilSpend = maxGil <= 0 ? null : maxGil;
            _editor.SavePurchaseConfiguration();
            InvalidateEstimate();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("0 means unlimited.");
        ImGui.SameLine();
        var currentWorldOnly = list.CurrentWorldOnly;
        if (ImGui.Checkbox("Current World Only##purchaseCurrentWorld", ref currentWorldOnly))
        {
            list.CurrentWorldOnly = currentWorldOnly;
            _editor.SavePurchaseConfiguration();
            InvalidateEstimate();
            StartAvailabilityProbe();
        }

        var estimateLabel = _estimateTask == null ? "Estimate" : "Estimating...";
        var planReady = _estimate?.Planning?.SelectedPlan != null;
        var estimateWidth = ImGui.CalcTextSize(estimateLabel).X + ImGui.GetStyle().FramePadding.X * 2;
        var planWidth = ImGui.CalcTextSize("Purchase Plan").X + ImGui.GetStyle().FramePadding.X * 2;
        ImGui.SameLine(Math.Max(ImGui.GetCursorPosX(), ImGui.GetWindowContentRegionMax().X - estimateWidth - planWidth - ImGui.GetStyle().ItemSpacing.X));
        using (ImRaii.Disabled(_estimateTask != null))
        {
            if (ImGui.Button(estimateLabel))
                StartEstimate();
        }
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && !string.IsNullOrWhiteSpace(_status))
            ImGui.SetTooltip(_status);
        ImGui.SameLine();
        using (ImRaii.Disabled(!planReady))
        {
            if (ImGui.Button("Purchase Plan") && _estimate?.Planning?.SelectedPlan is { } plan)
                GatherBuddy.CraftingPurchasePlanWindow?.OpenOrRestore(_editor, plan, _configuration?.Input.Dependencies ?? Array.Empty<AcquisitionDependency>());
        }
    }

    private void RefreshConfiguration()
    {
        if (_editor == null)
            return;
        try
        {
            _configuration = AcquisitionPlanningInputBuilder.BuildConfiguration(
                CraftingExecutionPlan.Create(_editor.CreatePlanningSnapshot()));
            _nextVendorRefresh = DateTime.UtcNow.AddMilliseconds(500);
        }
        catch (Exception exception)
        {
            _status = $"Purchase sources unavailable: {exception.Message}";
            GatherBuddy.Log.Warning($"[PurchaseConfiguration] Source capture failed: {exception}");
        }
    }

    private void StartAvailabilityProbe()
    {
        CancelAvailability();
        if (_editor == null || GatherBuddy.MarketboardService == null)
            return;
        RefreshConfiguration();
        var itemIds = GetBlockedDependencies(_configuration?.Input.Dependencies ?? Array.Empty<AcquisitionDependency>())
            .Select(dependency => dependency.ItemId)
            .Distinct()
            .ToArray();
        if (itemIds.Length == 0)
            return;
        var scope = _editor.PurchaseConfigurationList.CurrentWorldOnly
            ? GatherBuddy.MarketboardService.GetCurrentWorld()
            : GatherBuddy.MarketboardService.GetDataCenter();
        var cancellation = new CancellationTokenSource();
        _marketAvailability = new Dictionary<uint, MarketAvailability>();
        _availabilityCancellation = cancellation;
        _availabilityTask = GatherBuddy.MarketboardService.CheckAvailabilityAsync(scope, itemIds, cancellation.Token);
    }

    private void ConsumeAvailability()
    {
        var task = _availabilityTask;
        if (task == null || !task.IsCompleted)
            return;
        _availabilityTask = null;
        var cancellation = _availabilityCancellation;
        _availabilityCancellation = null;
        try
        {
            if (!task.IsCanceled && !task.IsFaulted)
                _marketAvailability = task.GetAwaiter().GetResult();
            else if (task.IsFaulted)
            {
                _ = task.Exception;
                _status = "Market availability could not be checked.";
            }
        }
        finally
        {
            cancellation?.Dispose();
        }
    }

    private void CancelAvailability()
    {
        ObserveFault(_availabilityTask);
        _availabilityCancellation?.Cancel();
        _availabilityCancellation?.Dispose();
        _availabilityCancellation = null;
        _availabilityTask = null;
    }

    private void StartEstimate()
    {
        if (_editor == null)
            return;
        InvalidateEstimate();
        var cancellation = new CancellationTokenSource();
        _estimateCancellation = cancellation;
        _estimateTask = EvaluateAsync(_editor.CreatePlanningSnapshot(), cancellation.Token);
        _status = "Estimating purchase plan...";
        GatherBuddy.CraftingPurchasePlanWindow?.Invalidate(_editor);
    }

    private static async Task<CraftingAcquisitionService.Evaluation> EvaluateAsync(
        CraftingListDefinition snapshot,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(EstimateTimeout);
        try
        {
            while (true)
            {
                var capture = await GatherBuddy.RunOnFrameworkThreadAsync(
                    () => CraftingAcquisitionService.Capture(CraftingExecutionPlan.Create(snapshot)), timeout.Token).ConfigureAwait(false);
                if (!capture.Snapshot.IsLoading)
                    return await Task.Run(() => CraftingAcquisitionService.Evaluate(capture, timeout.Token), timeout.Token).ConfigureAwait(false);
                await Task.Delay(250, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException($"Estimate did not complete within {EstimateTimeout.TotalSeconds:0} seconds.");
        }
    }

    private void ConsumeEstimate()
    {
        var task = _estimateTask;
        if (task == null || !task.IsCompleted)
            return;
        _estimateTask = null;
        var cancellation = _estimateCancellation;
        _estimateCancellation = null;
        try
        {
            if (task.IsCanceled)
                return;
            if (task.IsFaulted)
            {
                var exception = task.Exception?.GetBaseException();
                _ = task.Exception;
                _status = exception?.Message ?? "Estimate failed.";
                return;
            }
            _estimate = task.GetAwaiter().GetResult();
            _status = _estimate.Status;
            if (_estimate.Planning != null)
                _editor?.PublishAcquisitionPlanningResult(_estimate.Planning);
        }
        finally
        {
            cancellation?.Dispose();
        }
    }

    private void InvalidateEstimate()
    {
        ObserveFault(_estimateTask);
        _estimateCancellation?.Cancel();
        _estimateCancellation?.Dispose();
        _estimateCancellation = null;
        _estimateTask = null;
        _estimate = null;
        _status = string.Empty;
        if (_editor != null)
        {
            _editor.PublishAcquisitionPlanningResult(null);
            GatherBuddy.CraftingPurchasePlanWindow?.Invalidate(_editor);
        }
    }

    private static void ObserveFault(Task? task)
    {
        if (task == null)
            return;
        if (task.IsFaulted)
        {
            _ = task.Exception;
            return;
        }
        if (!task.IsCompleted)
            _ = task.ContinueWith(completed => _ = completed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
    }

    private static IEnumerable<AcquisitionDependency> GetBlockedDependencies(
        IEnumerable<AcquisitionDependency> dependencies)
        => dependencies.Where(dependency => dependency.RequiredQuantity > 0
            && (!dependency.IsFinalOutput || dependency.IsIntermediateDemand)
            && dependency.SelectedPath?.Capability.Status != AcquisitionCapabilityStatus.Usable);

    public void Dispose()
    {
        CancelAvailability();
        InvalidateEstimate();
    }
}

public sealed class CraftingPurchasePlanWindow : Window
{
    internal sealed record PreviewRow(
        uint ItemId,
        string ItemName,
        uint ClassJobId,
        string Source,
        IReadOnlyList<AcquisitionCurrencyCost> Costs);

    internal sealed record ItemEstimateRow(
        uint ItemId,
        string ItemName,
        IReadOnlyList<AcquisitionCurrencyCost> Costs);

    private CraftingListEditor? _editor;
    private IReadOnlyList<PreviewRow> _rows = Array.Empty<PreviewRow>();
    private IReadOnlyList<AcquisitionCurrencyRequirement> _totals = Array.Empty<AcquisitionCurrencyRequirement>();
    private bool? _pendingCollapseState;
    private bool _requestFocus;

    public CraftingPurchasePlanWindow()
        : base("Purchase Plan###CraftingPurchasePlan")
    {
        IsOpen = false;
        Size = VulcanUiScaling.Scaled(650f, 380f);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = VulcanUiScaling.Scaled(480f, 260f),
            MaximumSize = VulcanUiScaling.Scaled(1200f, 1200f),
        };
    }

    public void SetEditor(CraftingListEditor? editor)
    {
        if (ReferenceEquals(_editor, editor))
            return;
        _editor = editor;
        _rows = Array.Empty<PreviewRow>();
        _totals = Array.Empty<AcquisitionCurrencyRequirement>();
        if (editor == null)
            IsOpen = false;
    }

    public void Invalidate(CraftingListEditor editor)
    {
        if (!ReferenceEquals(_editor, editor))
            return;
        _rows = Array.Empty<PreviewRow>();
        _totals = Array.Empty<AcquisitionCurrencyRequirement>();
    }

    public void OpenOrRestore(
        CraftingListEditor editor,
        AcquisitionPlan plan,
        IReadOnlyList<AcquisitionDependency> dependencies)
    {
        SetEditor(editor);
        _rows = BuildRows(plan, dependencies);
        _totals = plan.Estimate.Currencies;
        IsOpen = true;
        _pendingCollapseState = false;
        _requestFocus = true;
    }

    internal static IReadOnlyList<PreviewRow> BuildRows(
        AcquisitionPlan plan,
        IReadOnlyList<AcquisitionDependency> dependencies)
    {
        var classJobs = dependencies
            .GroupBy(dependency => dependency.ItemId)
            .ToDictionary(group => group.Key, group => group.First().SelectedPath?.JobId ?? 0u);
        return plan.Transactions
            .GroupBy(transaction => new
            {
                transaction.ItemId,
                transaction.ItemName,
                transaction.SourceKind,
                transaction.WorldId,
                transaction.WorldName,
            })
            .Select(group => new PreviewRow(
                group.Key.ItemId,
                group.Key.ItemName,
                classJobs.GetValueOrDefault(group.Key.ItemId),
                group.Key.SourceKind == AcquisitionSourceKind.Vendor
                    ? "Vendor"
                    : $"{group.Key.WorldName} marketplace",
                SumCosts(group)))
            .OrderBy(row => row.ItemName, StringComparer.Ordinal)
            .ThenBy(row => row.Source, StringComparer.Ordinal)
            .ToArray();
    }

    internal static IReadOnlyList<ItemEstimateRow> BuildItemEstimates(AcquisitionPlan plan)
        => plan.Transactions
            .GroupBy(transaction => new { transaction.ItemId, transaction.ItemName })
            .OrderBy(group => group.Key.ItemName, StringComparer.Ordinal)
            .Select(group => new ItemEstimateRow(group.Key.ItemId, group.Key.ItemName, SumCosts(group)))
            .ToArray();

    private static IReadOnlyList<AcquisitionCurrencyCost> SumCosts(IEnumerable<AcquisitionTransaction> transactions)
        => transactions
            .SelectMany(transaction => transaction.Costs)
            .GroupBy(cost => cost.CurrencyId)
            .Select(costs => new AcquisitionCurrencyCost
            {
                CurrencyId = costs.Key,
                IconId = costs.First().IconId,
                CurrencyName = costs.First().CurrencyName,
                Amount = costs.Sum(cost => cost.Amount),
                IsGil = costs.First().IsGil,
                IsSpecialCurrency = costs.First().IsSpecialCurrency,
                Group = costs.First().Group,
            })
            .OrderBy(cost => cost.CurrencyId)
            .ToArray();

    public override void PreDraw()
    {
        if (_editor != null)
            WindowName = $"Purchase Plan: {_editor.ListName}###CraftingPurchasePlan";
        if (_pendingCollapseState.HasValue)
        {
            ImGui.SetNextWindowCollapsed(_pendingCollapseState.Value, ImGuiCond.Always);
            _pendingCollapseState = null;
        }
        if (_requestFocus)
        {
            ImGui.SetNextWindowFocus();
            _requestFocus = false;
        }
    }

    public override void Draw()
    {
        using var theme = VulcanUiStyle.PushTheme();
        if (_editor == null || _rows.Count == 0)
        {
            ImGui.TextDisabled("Run Estimate to create a purchase plan.");
            return;
        }

        var footerHeight = ImGui.GetTextLineHeightWithSpacing() + 1f;
        if (ImGui.BeginTable("##purchasePlanRows", 3,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.ScrollY,
            new Vector2(0, -footerHeight)))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch, 2f);
            ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthStretch, 1.4f);
            ImGui.TableSetupColumn("Currency", ImGuiTableColumnFlags.WidthStretch, 1.2f);
            ImGui.TableHeadersRow();
            foreach (var row in _rows)
            {
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                DrawPreviewItem(row);
                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted(row.Source);
                ImGui.TableSetColumnIndex(2);
                DrawCosts(row.Costs);
            }
            ImGui.EndTable();
        }
        ImGui.Separator();
        DrawTotals();
    }

    private static void DrawPreviewItem(PreviewRow row)
    {
        var size = VulcanUiScaling.Scaled(24f, 24f);
        DrawItemIcon(row.ItemId, size);
        ImGui.SameLine();
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + Math.Max(0f, (size.Y - ImGui.GetTextLineHeight()) / 2f));
        ImGui.TextUnformatted(row.ItemName);
        if (row.ClassJobId != 0)
        {
            ImGui.SameLine();
            CraftingRowIcons.DrawIconsRightAligned(new[] { CraftingRowIcons.GetClassJobIcon(row.ClassJobId) }, VulcanUiScaling.Scaled(17f));
        }
    }

    internal static void DrawEstimateItem(ItemEstimateRow row)
    {
        var rowTop = ImGui.GetCursorPosY();
        var rightEdge = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
        DrawItemIcon(row.ItemId, VulcanUiScaling.Scaled(20f, 20f));
        ImGui.SameLine();
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted(row.ItemName);
        DrawCostsRightAligned(row.Costs, rightEdge, rowTop, VulcanUiScaling.Scaled(20f));
    }

    private static void DrawItemIcon(uint itemId, Vector2 size)
    {
        var itemSheet = Dalamud.GameData.GetExcelSheet<Item>();
        var iconId = itemSheet?.TryGetRow(itemId, out var item) == true ? (uint)item.Icon : 0u;
        if (iconId != 0 && Icons.DefaultStorage.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId)).TryGetWrap(out var wrap, out _))
            ImGui.Image(wrap.Handle, size);
        else
            ImGui.Dummy(size);
    }

    private static void DrawCosts(IReadOnlyList<AcquisitionCurrencyCost> costs)
    {
        for (var i = 0; i < costs.Count; i++)
        {
            if (i > 0)
                ImGui.SameLine(0, VulcanUiScaling.Scaled(10f));
            DrawCurrency(costs[i].IconId, costs[i].Amount, costs[i].CurrencyName);
        }
    }

    internal static void DrawCostsRightAligned(
        IReadOnlyList<AcquisitionCurrencyCost> costs,
        float rightEdge,
        float rowTop,
        float rowHeight)
    {
        if (costs.Count == 0)
            return;
        var iconSize = VulcanUiScaling.Scaled(18f);
        var width = costs.Sum(cost => iconSize + VulcanUiScaling.Scaled(4f)
            + ImGui.CalcTextSize(cost.Amount.ToString("N0")).X)
            + VulcanUiScaling.Scaled(10f) * (costs.Count - 1);
        var leftEdge = ImGui.GetItemRectMax().X - ImGui.GetWindowPos().X + ImGui.GetStyle().ItemSpacing.X;
        ImGui.SetCursorPos(new Vector2(
            Math.Max(leftEdge, rightEdge - width),
            rowTop + Math.Max(0f, (rowHeight - iconSize) / 2f)));
        DrawCosts(costs);
    }

    private void DrawTotals()
    {
        ImGui.TextUnformatted("Totals:");
        foreach (var total in _totals)
        {
            var width = VulcanUiScaling.Scaled(24f) + ImGui.CalcTextSize(total.Required.ToString("N0")).X
                + ImGui.GetStyle().ItemSpacing.X * 2;
            if (ImGui.GetContentRegionAvail().X < width)
                ImGui.NewLine();
            else
                ImGui.SameLine(0, VulcanUiScaling.Scaled(16f));
            DrawCurrency(total.IconId, total.Required, total.CurrencyName);
        }
    }

    internal static void DrawCurrency(uint iconId, long amount, string tooltip)
    {
        var resolvedIcon = iconId != 0 ? iconId : ResolveGilIconId();
        var size = VulcanUiScaling.Scaled(18f, 18f);
        var startY = ImGui.GetCursorPosY();
        if (resolvedIcon != 0 && Icons.DefaultStorage.TextureProvider.GetFromGameIcon(new GameIconLookup(resolvedIcon)).TryGetWrap(out var wrap, out _))
            ImGui.Image(wrap.Handle, size);
        else
            ImGui.Dummy(size);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(tooltip);
        ImGui.SameLine(0, VulcanUiScaling.Scaled(4f));
        ImGui.SetCursorPosY(startY + Math.Max(0f, (size.Y - ImGui.GetTextLineHeight()) / 2f));
        ImGui.TextUnformatted(amount.ToString("N0"));
    }

    private static uint ResolveGilIconId()
        => Dalamud.GameData.GetExcelSheet<Item>()?.TryGetRow(1, out var gil) == true ? (uint)gil.Icon : 0u;
}
