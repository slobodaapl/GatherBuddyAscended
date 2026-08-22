using System;
using System.Collections.Generic;
using System.Linq;
using GatherBuddy.Vulcan;

namespace GatherBuddy.Crafting;

public enum CraftingAutomationOwner
{
    GatherBuddy,
    ArtisanIpc,
    FcFulfillment,
}

public enum CraftingStartupRecoveryDecision
{
    Wait,
    Start,
    Discard,
}

/// <summary>
/// Non-authoritative identity retained for an FC recovery ticket. The proof
/// itself is deliberately not persisted: recovery must rebuild it from the
/// current request, worker session, and capability fingerprints.
/// </summary>
public sealed class FcCapabilityRecoveryIdentity
{
    public Guid RequestId { get; set; }
    public Guid SessionId { get; set; }
    public ulong SessionGeneration { get; set; }
    public bool RequiresHq { get; set; } = true;
    public List<Guid> Lists { get; set; } = new();

    internal bool IsValid
        => SessionId != Guid.Empty
            && SessionGeneration != 0
            && Lists is { Count: > 0 }
            && Lists.All(value => value != Guid.Empty)
            && Lists.Distinct().Count() == Lists.Count
            && Lists.SequenceEqual(Lists.OrderBy(value => value));
}

public sealed class CraftingRecoveryItem
{
    public uint RecipeId { get; set; }
    public bool Skipping { get; set; }
    public bool NQOnly { get; set; }
    public bool IsOriginalRecipe { get; set; }
    public Dictionary<uint, int> IngredientPreferences { get; set; } = new();
    public CraftingListConsumableOverrides ConsumableOverrides { get; set; } = new();
    public RecipeCraftSettings? CraftSettings { get; set; }
    public DonatelloExecutionOptions? DonatelloOptions { get; set; }

    internal static CraftingRecoveryItem Capture(CraftingListItem item)
        => new()
        {
            RecipeId = item.RecipeId,
            Skipping = item.Options.Skipping,
            NQOnly = item.Options.NQOnly,
            IsOriginalRecipe = item.IsOriginalRecipe,
            IngredientPreferences = new Dictionary<uint, int>(item.IngredientPreferences),
            ConsumableOverrides = item.ConsumableOverrides.Clone(),
            CraftSettings = item.CraftSettings?.Clone(),
            DonatelloOptions = item.CraftSettings?.DonatelloOptions,
        };

    internal CraftingListItem Restore()
    {
        var settings = CraftSettings?.Clone();
        if (DonatelloOptions != null)
        {
            settings ??= new RecipeCraftSettings();
            settings.DonatelloOptions = DonatelloOptions;
        }

        return new CraftingListItem(RecipeId, 1)
        {
            Options = new ListItemOptions
            {
                Skipping = Skipping,
                NQOnly = NQOnly,
            },
            IngredientPreferences = new Dictionary<uint, int>(IngredientPreferences ?? new()),
            ConsumableOverrides = ConsumableOverrides?.Clone() ?? new CraftingListConsumableOverrides(),
            IsOriginalRecipe = IsOriginalRecipe,
            CraftSettings = settings,
        };
    }
}

public sealed class CraftingRecoveryTicket
{
    public const int CurrentVersion = 1;
    public static readonly TimeSpan StartupProbeTimeout = TimeSpan.FromSeconds(15);

    public int Version { get; set; } = CurrentVersion;
    public CraftingAutomationOwner Owner { get; set; }
    public ExecutionSource Source { get; set; } = ExecutionSource.PrivateList;
    public FcCapabilityRecoveryIdentity? FcCapability { get; set; }
    public List<CraftingRecoveryItem> RemainingQueue { get; set; } = new();
    public CraftingListConsumableSettings? ListConsumables { get; set; }

    internal ExecutionSource EffectiveSource
        => Source == ExecutionSource.FcFulfillment || Owner == CraftingAutomationOwner.FcFulfillment
            ? ExecutionSource.FcFulfillment
            : ExecutionSource.PrivateList;

    internal bool IsFcOwned => EffectiveSource == ExecutionSource.FcFulfillment;

    internal static CraftingRecoveryTicket Capture(
        CraftingAutomationOwner owner,
        IEnumerable<CraftingListItem> remainingQueue,
        CraftingListConsumableSettings? listConsumables,
        ExecutionSource source = ExecutionSource.PrivateList,
        FcCapabilityRecoveryIdentity? fcCapability = null)
        => new()
        {
            Owner = owner,
            Source = source,
            FcCapability = fcCapability,
            RemainingQueue = remainingQueue.Select(CraftingRecoveryItem.Capture).ToList(),
            ListConsumables = listConsumables?.Clone(),
        };

    internal bool TryRestore(out List<CraftingListItem> queue, out string failureReason)
    {
        queue = [];
        if (Version != CurrentVersion)
        {
            failureReason = $"unsupported recovery ticket version {Version}";
            return false;
        }
        if (RemainingQueue == null || RemainingQueue.Count == 0 || RemainingQueue.Any(item => item == null || item.RecipeId == 0))
        {
            failureReason = "recovery ticket has no valid remaining queue";
            return false;
        }

        queue = RemainingQueue.Select(item => item.Restore()).ToList();
        failureReason = string.Empty;
        return true;
    }

    internal static CraftingStartupRecoveryDecision DecideStartupRecovery(
        CraftingRecoveryTicket ticket,
        bool playerAvailable,
        bool synthesisOpen,
        uint? activeRecipeId,
        TimeSpan probeElapsed)
    {
        if (ticket.Version != CurrentVersion
            || ticket.RemainingQueue == null
            || ticket.RemainingQueue.Count == 0
            || ticket.RemainingQueue[0] == null
            || ticket.RemainingQueue[0].RecipeId == 0)
            return CraftingStartupRecoveryDecision.Discard;
        if (!playerAvailable)
            return CraftingStartupRecoveryDecision.Wait;
        if (!synthesisOpen)
            return probeElapsed >= StartupProbeTimeout
                ? CraftingStartupRecoveryDecision.Discard
                : CraftingStartupRecoveryDecision.Wait;
        if (!activeRecipeId.HasValue)
            return probeElapsed >= StartupProbeTimeout
                ? CraftingStartupRecoveryDecision.Discard
                : CraftingStartupRecoveryDecision.Wait;
        return activeRecipeId.Value == ticket.RemainingQueue[0].RecipeId
            ? CraftingStartupRecoveryDecision.Start
            : CraftingStartupRecoveryDecision.Discard;
    }
}
