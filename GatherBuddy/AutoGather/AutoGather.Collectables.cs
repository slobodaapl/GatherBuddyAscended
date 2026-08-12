using GatherBuddy.Helpers;
using FFXIVClientStructs.FFXIV.Client.UI;
using GatherBuddy.AutoGather.Extensions;
using GatherBuddy.AutoGather.AtkReaders;
using GatherBuddy.AutoGather.Collectables;
using GatherBuddy.Classes;
using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Objects.SubKinds;
using GatherBuddy.Vulcan;
using Lumina.Excel.Sheets;

namespace GatherBuddy.AutoGather
{
    public partial class AutoGather
    {
        private CollectableRotation? CurrentCollectableRotation;

        private unsafe bool HasCollectables()
        {
            if (!GatherBuddy.Config.CollectableConfig.AutoTurnInCollectables
             || !CollectableTurnInRequirements.IsAvailable)
                return false;

            if (GatherBuddy.CollectableManager == null)
                return false;
            var thresholdState = CollectableInventoryHelper.GetThresholdState(GatherBuddy.Config.CollectableConfig);
            if (!thresholdState.ThresholdReached)
                return false;
            if (thresholdState.InventoryFullMode)
                GatherBuddy.Log.Debug($"[HasCollectables] Inventory threshold reached ({thresholdState.UsedSlots}/{thresholdState.TotalSlots}) with {thresholdState.CollectableCount} collectables - triggering turn-in");
            else
                GatherBuddy.Log.Debug($"[HasCollectables] Collectable threshold reached ({thresholdState.CollectableCount}) - triggering turn-in");

            return true;
        }

        private unsafe class CollectableRotation
        {
            public CollectableRotation(ConfigPreset config, Gatherable item, uint quantity, int gatherChance)
            {
                this.config = config;
                shouldUseFullRotation = Player.Object?.CurrentGp >= config.CollectableActionsMinGP;
                this.item = item;
                this.quantity = quantity;
                this.gatherChance = gatherChance;
            }

            private readonly bool shouldUseFullRotation = false;
            private readonly ConfigPreset config;
            private readonly Gatherable item;
            private readonly uint quantity;
            private readonly int gatherChance;
            private int? previousIntegrity;
            private bool revisitUsed;

            private static bool ShouldAbandonCompletedCollectable(Gatherable collectable)
                => GatherBuddy.Config.AutoGatherConfig.AbandonNodes
                && !(GatherBuddy.Config.AutoGatherConfig.AlwaysExhaustTimedCollectableNodes
                  && collectable.NodeType is Enums.NodeType.Unspoiled or Enums.NodeType.Legendary or Enums.NodeType.Clouded);

            public Actions.BaseAction GetNextAction(GatheringMasterpieceReader masterpieceReader)
            {
                try
                {
                    return GetNativeNextAction(masterpieceReader);
                }
                catch (Exception exception) when (exception is DllNotFoundException
                                                       or EntryPointNotFoundException
                                                       or BadImageFormatException
                                                       or InvalidOperationException)
                {
                    throw new CollectableSolverException(exception.Message, exception);
                }
            }

            private Actions.BaseAction GetNativeNextAction(GatheringMasterpieceReader masterpieceReader)
            {
                var player = Player.Object ?? throw new InvalidOperationException("Player object is null");
                var itemsLeft = (int)Math.Max(0L, (long)quantity - item.GetTotalCount());
                if (itemsLeft <= 0 && ShouldAbandonCompletedCollectable(item))
                    throw new NoGatherableItemsInNodeException();

                if (previousIntegrity == 1 && masterpieceReader.IntegrityCurrent == masterpieceReader.IntegrityMax)
                    revisitUsed = true;
                previousIntegrity = masterpieceReader.IntegrityCurrent;

                var (targetScore, minScore) = GetCollectabilityScores(masterpieceReader);
                var (rewards, unsupportedReason) = ResolveRewards(masterpieceReader);
                var mode = config.ChooseBestActionsAutomatically
                    ? config.CollectableSolver
                    : CollectableSolverMode.Legacy;
                var scourGain = masterpieceReader.ScourGain;
                if (Player.Status.Any(status => status.StatusId == Actions.Scrutiny.EffectId))
                    scourGain = Math.Max(1, scourGain / 2);
                var brazenGains = Enumerable.Range(0, 11)
                    .Select(index => new GatheringWeightedGain(scourGain / 2 + scourGain * index / 10, 1))
                    .ToArray();
                var standard = Player.Status.Any(status => status.StatusId == 3911) ? 2
                    : Player.Status.Any(status => status.StatusId == 2418) ? 1
                    : 0;
                var automatic = config.ChooseBestActionsAutomatically;
                var request = new GatheringSolveRequest(
                    mode,
                    new GatheringSolverState(
                        masterpieceReader.CollectabilityCurrent,
                        masterpieceReader.IntegrityCurrent,
                        masterpieceReader.IntegrityMax,
                        (int)player.CurrentGp,
                        (int)player.MaxGp,
                        itemsLeft,
                        Player.Status.Any(status => status.StatusId == Actions.Scrutiny.EffectId),
                        Player.Status.Any(status => status.StatusId == Actions.CollectorsFocus.EffectId),
                        Player.Status.Any(status => status.StatusId == Actions.PrimingTouch.EffectId),
                        standard,
                        Player.Status.Any(status => status.StatusId == Actions.SolidAge.EffectId),
                        revisitUsed),
                    rewards,
                    new GatheringActionModel(
                        scourGain,
                        scourGain * 3 / 4,
                        brazenGains,
                        Actions.Scrutiny.GpCost,
                        Actions.CollectorsFocus.GpCost,
                        Actions.PrimingTouch.GpCost,
                        Actions.SolidAge.GpCost,
                        IsEnabled(Actions.Scour, config.CollectableActions.Scour, automatic),
                        IsEnabled(Actions.Brazen, config.CollectableActions.Brazen, automatic),
                        IsEnabled(Actions.Meticulous, config.CollectableActions.Meticulous, automatic),
                        IsEnabled(Actions.Scrutiny, config.CollectableActions.Scrutiny, automatic),
                        automatic && player.Level >= Actions.CollectorsFocus.MinLevel,
                        automatic && player.Level >= Actions.PrimingTouch.MinLevel,
                        IsEnabled(Actions.SolidAge, config.CollectableActions.SolidAge, automatic),
                        player.Level >= Actions.Wise.MinLevel),
                    new GatheringMechanics(
                        Math.Clamp(gatherChance, 0, 100) * 100,
                        4000,
                        player.Level >= 100 ? 10000 : 7000,
                        100,
                        2000,
                        player.Level >= 100 ? 2000 : 0,
                        2500,
                        4000,
                        2,
                        5000,
                        player.Level >= 91 ? 1000 : 0,
                        6,
                        20000,
                        500000),
                    new GatheringLegacyOptions(
                        targetScore,
                        minScore,
                        shouldUseFullRotation,
                        config.CollectableAlwaysUseSolidAge,
                        ShouldAbandonCompletedCollectable(item)),
                    mode == CollectableSolverMode.ExpectedScrip ? unsupportedReason : null);
                var decision = DonatelloNative.SolveGathering(request);
                if (decision.FallbackReason != null)
                    GatherBuddy.Log.Debug($"[AutoGather] Expected-scrip solver used legacy fallback for {item.Name}: {decision.FallbackReason}");
                return MapAction(decision.Action);
            }

            private static bool IsEnabled(Actions.BaseAction action, ConfigPreset.ActionConfig actionConfig, bool automatic)
                => Player.Level >= action.MinLevel && (automatic || actionConfig.Enabled);

            private static Actions.BaseAction MapAction(GatheringSolverAction action)
                => action switch
                {
                    GatheringSolverAction.Scour => Actions.Scour,
                    GatheringSolverAction.Brazen => Actions.Brazen,
                    GatheringSolverAction.Meticulous => Actions.Meticulous,
                    GatheringSolverAction.Scrutiny => Actions.Scrutiny,
                    GatheringSolverAction.CollectorsFocus => Actions.CollectorsFocus,
                    GatheringSolverAction.PrimingTouch => Actions.PrimingTouch,
                    GatheringSolverAction.SolidReason => Actions.SolidAge,
                    GatheringSolverAction.WiseToTheWorld => Actions.Wise,
                    GatheringSolverAction.Collect => Actions.Collect,
                    _ => throw new InvalidOperationException($"Unsupported native gathering action {action}"),
                };

            private (IReadOnlyList<GatheringRewardTier> Rewards, string? UnsupportedReason) ResolveRewards(
                GatheringMasterpieceReader masterpieceReader)
            {
                var fallback = new[] { new GatheringRewardTier(Math.Max(1, masterpieceReader.LowThreshold), 1) };
                if (item.ItemData.AetherialReduce > 0)
                    return (fallback, "aetherial reduction collectable");
                if (item.ItemData.AdditionalData.RowId != 0 || item.GatheringData.Unknown3 is 3 or 4 or 6)
                    return (fallback, "non-scrip collectable");

                var rows = Dalamud.GameData.GetSubrowExcelSheet<CollectablesShopItem>()?
                    .SelectMany(group => group)
                    .Where(row => row.Item.RowId == item.ItemId
                               && row.CollectablesShopRefine.RowId != 0
                               && row.CollectablesShopRewardScrip.RowId != 0)
                    .ToArray() ?? [];
                if (rows.Length != 1)
                    return (fallback, $"expected one scrip reward row, found {rows.Length}");

                var refine = rows[0].CollectablesShopRefine.Value;
                var reward = rows[0].CollectablesShopRewardScrip.Value;
                var rewards = new[]
                {
                    new GatheringRewardTier((int)refine.LowCollectability, (int)reward.LowReward),
                    new GatheringRewardTier((int)refine.MidCollectability, (int)reward.MidReward),
                    new GatheringRewardTier((int)refine.HighCollectability, (int)reward.HighReward),
                }.Where(tier => tier.Threshold > 0 && tier.Scrip > 0)
                 .DistinctBy(tier => tier.Threshold)
                 .OrderBy(tier => tier.Threshold)
                 .ToArray();
                var visibleThresholds = new[]
                {
                    masterpieceReader.LowThreshold,
                    masterpieceReader.MidThreshold,
                    masterpieceReader.HighThreshold,
                }.Where(threshold => threshold > 0).Distinct().Order().ToArray();
                if (rewards.Length == 0 || !rewards.Select(tier => tier.Threshold).SequenceEqual(visibleThresholds))
                    return (fallback, "reward thresholds do not match the live gathering window");
                return (rewards, null);
            }

            private Actions.BaseAction GetLegacyNextAction(GatheringMasterpieceReader masterpieceReader)
            {
                var player = Player.Object ?? throw new InvalidOperationException("Player object is null");
                var itemsLeft = (int)Math.Max(0L, (long)quantity - item.GetTotalCount());

                if (itemsLeft <= 0 && ShouldAbandonCompletedCollectable(item))
                    throw new NoGatherableItemsInNodeException();

                int collectability   = masterpieceReader.CollectabilityCurrent;
                int currentIntegrity = masterpieceReader.IntegrityCurrent;
                int maxIntegrity     = masterpieceReader.IntegrityMax;
                int scourColl        = masterpieceReader.ScourGain;
                int meticulousColl   = masterpieceReader.MeticulousGain;
                int brazenColl       = masterpieceReader.BrazenGainMax;

                if (ShouldUseWise(currentIntegrity, maxIntegrity))
                    return Actions.Wise;

                var (targetScore, minScore) = GetCollectabilityScores(masterpieceReader);

                if (collectability >= targetScore)
                {
                    if ((shouldUseFullRotation || config.CollectableAlwaysUseSolidAge)
                     && ShouldSolidAgeCollectables(player, currentIntegrity, maxIntegrity, itemsLeft))
                        return Actions.SolidAge;
                    else
                        return Actions.Collect;
                }

                if (currentIntegrity == 1
                 && collectability >= minScore)
                    return Actions.Collect;

                if (shouldUseFullRotation && NeedScrutiny(player, collectability, scourColl, meticulousColl, brazenColl, targetScore) && ShouldUseScrutiny(player))
                    return Actions.Scrutiny;

                if (meticulousColl + collectability >= targetScore
                 && ShouldUseMeticulous(player))
                    return Actions.Meticulous;

                if (Player.Status.Any(s => s.StatusId == 3911 /*Collector's High Standard*/) && ShouldUseBrazen(player))
                    return Actions.Brazen;

                if (scourColl + collectability >= targetScore
                 && ShouldUseScour(player))
                    return Actions.Scour;

                if (ShouldUseMeticulous(player))
                    return Actions.Meticulous;

                //Fallback path if some actions are disabled.
                if (Player.Status.Any(s => s.StatusId == 2418 /*Collector's Standard*/) && ShouldUseBrazen(player))
                    return Actions.Brazen;
                if (ShouldUseScour(player))
                    return Actions.Scour;
                if (ShouldUseBrazen(player))
                    return Actions.Brazen;

                throw new NoCollectableActionsException();
            }

            private (int targetScore, int minScore) GetCollectabilityScores(GatheringMasterpieceReader masterpieceReader)
            {
                if (config.CollectableManualScores)
                    return (config.CollectableTagetScore, config.CollectableMinScore);

                int targetScore, minScore;

                // Check reward tiers in descending order and use the first visible one for target score
                if (masterpieceReader.HighThreshold > 0)
                    targetScore = masterpieceReader.HighThreshold;
                else if (masterpieceReader.MidThreshold > 0)
                    targetScore = masterpieceReader.MidThreshold;
                else
                    targetScore = masterpieceReader.LowThreshold;

                // For minScore, pick the lowest non-zero threshold
                int[] thresholds = { masterpieceReader.LowThreshold, masterpieceReader.MidThreshold, masterpieceReader.HighThreshold };
                minScore = thresholds.Where(t => t > 0).DefaultIfEmpty(1).Min();

                // For custom deliveries and quest items, we always want max collectability
                if (item.GatheringData.Unknown3 is 3 or 4 or 6)
                    minScore = targetScore;

                GatherBuddy.Log.Verbose($"Using target collectability {targetScore} and minimum collectability {minScore} for {item.Name}.");
                return (targetScore, minScore);
            }

            private bool NeedScrutiny(IPlayerCharacter player, int collectability, int scourColl, int meticulousColl, int brazenColl, int targetScore)
            {
                if (scourColl + collectability >= targetScore && ShouldUseScour(player))
                    return false;
                if (meticulousColl + collectability >= targetScore && ShouldUseMeticulous(player))
                    return false;
                if (brazenColl + collectability >= targetScore && ShouldUseBrazen(player))
                    return false;

                return true;
            }
            private bool ShouldUseMeticulous(IPlayerCharacter player)
            {
                if (player.Level < Actions.Meticulous.MinLevel)
                    return false;
                if (player.CurrentGp < Actions.Meticulous.GpCost)
                    return false;
                if (config.ChooseBestActionsAutomatically)
                    return true;
                if (player.CurrentGp < config.CollectableActions.Meticulous.MinGP
                 || player.CurrentGp > config.CollectableActions.Meticulous.MaxGP)
                    return false;

                return config.CollectableActions.Meticulous.Enabled;
            }

            private bool ShouldUseScour(IPlayerCharacter player)
            {
                if (player.Level < Actions.Brazen.MinLevel)
                    return false;
                if (player.CurrentGp < Actions.Brazen.GpCost)
                    return false;
                if (config.ChooseBestActionsAutomatically)
                    return true;
                if (player.CurrentGp < config.CollectableActions.Scour.MinGP
                 || player.CurrentGp > config.CollectableActions.Scour.MaxGP)
                    return false;

                return config.CollectableActions.Scour.Enabled;
            }

            private bool ShouldUseBrazen(IPlayerCharacter player)
            {
                if (player.Level < Actions.Meticulous.MinLevel)
                    return false;
                if (player.CurrentGp < Actions.Meticulous.GpCost)
                    return false;
                if (config.ChooseBestActionsAutomatically)
                    return true;
                if (player.CurrentGp < config.CollectableActions.Brazen.MinGP
                 || player.CurrentGp > config.CollectableActions.Brazen.MaxGP)
                    return false;

                return config.CollectableActions.Brazen.Enabled;
            }

            private bool ShouldUseScrutiny(IPlayerCharacter player)
            {
                if (player.Level < Actions.Scrutiny.MinLevel)
                    return false;
                if (player.CurrentGp < Actions.Scrutiny.GpCost)
                    return false;
                if (Player.Status.Any(s => s.StatusId == Actions.Scrutiny.EffectId))
                    return false;
                if (config.ChooseBestActionsAutomatically)
                    return true;
                if (player.CurrentGp < config.CollectableActions.Scrutiny.MinGP
                 || player.CurrentGp > config.CollectableActions.Scrutiny.MaxGP)
                    return false;

                return config.CollectableActions.Scrutiny.Enabled;
            }

            private bool ShouldSolidAgeCollectables(IPlayerCharacter player, int integrity, int maxIntegrity, int itemsLeft)
            {
                if (integrity > Math.Min(2, maxIntegrity - 1))
                    return false;
                if (itemsLeft <= integrity)
                    return false;
                if (player.Level < Actions.SolidAge.MinLevel)
                    return false;
                if (player.CurrentGp < Actions.SolidAge.GpCost)
                    return false;
                if (Player.Status.Any(s => s.StatusId == Actions.SolidAge.EffectId))
                    return false;
                if (config.ChooseBestActionsAutomatically)
                    return true;
                if (player.CurrentGp < config.CollectableActions.SolidAge.MinGP
                 || player.CurrentGp > config.CollectableActions.SolidAge.MaxGP)
                    return false;

                return config.CollectableActions.SolidAge.Enabled;
            }
        }
    }
}
