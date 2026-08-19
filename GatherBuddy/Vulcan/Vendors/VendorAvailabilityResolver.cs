using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace GatherBuddy.Vulcan.Vendors;

public enum VendorAvailabilityState
{
    Available,
    Locked,
    Unknown,
}

public readonly record struct VendorAvailability(VendorAvailabilityState State, string Reason)
{
    public bool IsAvailable => State == VendorAvailabilityState.Available;
}

/// <summary>
/// Result of one unlock query. Unknown is deliberately distinct from false:
/// an unresolved requirement must never be treated as safe to automate.
/// </summary>
public readonly record struct VendorAvailabilityCheck(
    bool   IsKnown,
    bool   IsSatisfied,
    string Description);

/// <summary>
/// Injectable game-state boundary for vendor availability. The production
/// implementation is backed by Dalamud/FFXIV state; tests can provide exact
/// requirement decisions without constructing game services.
/// </summary>
public interface IVendorAvailabilityQueries
{
    VendorAvailabilityCheck Quest(uint questId);
    VendorAvailabilityCheck Achievement(uint achievementId);
    VendorAvailabilityCheck Content(uint contentId, bool mustBeComplete);
    VendorAvailabilityCheck GrandCompany(uint companyId, uint requiredRank);
    VendorAvailabilityCheck AlliedSociety(uint societyId, uint requiredRank);
}

public static class VendorAvailabilityResolver
{
    public static VendorAvailability Resolve(VendorShopEntry entry, VendorNpc vendor)
        => Resolve(entry, vendor, new LiveVendorAvailabilityQueries());

    public static VendorAvailability Resolve(
        VendorShopEntry entry,
        VendorNpc vendor,
        IVendorAvailabilityQueries queries)
    {
        var questIds = (entry.RequiredQuestIds ?? [])
            .Append(vendor.UnlockQuestId)
            .Where(id => id != 0)
            .Distinct();
        foreach (var questId in questIds)
        {
            var check = queries.Quest(questId);
            var result = ResolveCheck(check, $"Required quest #{questId} could not be resolved.", "Requires quest");
            if (result.State != VendorAvailabilityState.Available)
                return result;
        }

        if (entry.RequiredAchievementId != 0)
        {
            var check = queries.Achievement(entry.RequiredAchievementId);
            var result = ResolveCheck(check, $"Required achievement #{entry.RequiredAchievementId} could not be resolved.", "Requires achievement");
            if (result.State != VendorAvailabilityState.Available)
                return result;
        }

        if (entry.RequiredContentId != 0)
        {
            var check = queries.Content(entry.RequiredContentId, entry.RequiredContentMustBeComplete);
            var result = ResolveCheck(
                check,
                $"Required content #{entry.RequiredContentId} could not be resolved.",
                entry.RequiredContentMustBeComplete ? "Requires completion of" : "Requires access to");
            if (result.State != VendorAvailabilityState.Available)
                return result;
        }

        if (entry.ShopType == VendorShopType.GrandCompanySeals
            || vendor.MenuShopType == VendorMenuShopType.GrandCompanyShop
            || vendor.RequiredGrandCompanyRank != 0)
        {
            if (vendor.MenuShopType != VendorMenuShopType.GrandCompanyShop
                || vendor.GcRankIndex < 0
                || vendor.GcCategoryIndex < 0)
                return new(VendorAvailabilityState.Locked,
                    "The selected Grand Company shop route or category is not valid.");

            if (!VendorOfferMath.HasValidCurrencyCosts(entry.CurrencyCosts))
                return new(VendorAvailabilityState.Unknown,
                    "Grand Company currency cost could not be resolved for this vendor offer.");

            if (!TryGetGrandCompanyId(entry, out var companyId))
                return new(VendorAvailabilityState.Unknown, "Grand Company membership could not be resolved for this vendor offer.");

            var check = queries.GrandCompany(companyId, vendor.RequiredGrandCompanyRank);
            var result = ResolveCheck(check, "Grand Company membership or rank could not be resolved.", "Requires");
            if (result.State != VendorAvailabilityState.Available)
                return result;
        }

        var societyId = entry.RequiredAlliedSocietyId != 0
            ? entry.RequiredAlliedSocietyId
            : vendor.RequiredAlliedSocietyId;
        var societyRank = entry.RequiredAlliedSocietyRank != 0
            ? entry.RequiredAlliedSocietyRank
            : vendor.RequiredAlliedSocietyRank;
        var isKnownNonAlliedScripExchange = vendor.MenuShopType == VendorMenuShopType.InclusionShop
            && entry.Group == VendorCurrencyGroup.Scrips
            && societyId == 0
            && societyRank == 0;
        if (isKnownNonAlliedScripExchange)
            return new(VendorAvailabilityState.Available, string.Empty);

        var hasAlliedRequirement = societyId != 0 || societyRank != 0 || !vendor.AlliedRequirementKnown;
        if (!hasAlliedRequirement)
            return new(VendorAvailabilityState.Available, string.Empty);

        if (societyId == 0
            || (!vendor.AlliedRequirementKnown && entry.RequiredAlliedSocietyId == 0))
            return new(VendorAvailabilityState.Unknown,
                "Allied-society identity or requirements for this vendor route could not be resolved safely.");

        var hasAuthoritativeQuestGate = (entry.RequiredQuestIds ?? []).Any(id => id != 0)
            || vendor.UnlockQuestId != 0;
        if (societyRank == 0 && !hasAuthoritativeQuestGate)
            return new(VendorAvailabilityState.Unknown,
                "Allied-society rank requirement is unresolved and cannot be verified safely.");

        if (societyRank != 0)
        {
            var alliedCheck = queries.AlliedSociety(societyId, societyRank);
            var alliedResult = ResolveCheck(alliedCheck, "Allied-society rank could not be resolved.", "Requires");
            if (alliedResult.State != VendorAvailabilityState.Available)
                return alliedResult;
        }

        return new(VendorAvailabilityState.Available, string.Empty);
    }

    private static bool TryGetGrandCompanyId(VendorShopEntry entry, out byte companyId)
    {
        companyId = 0;
        var found = false;
        foreach (var cost in entry.CurrencyCosts)
        {
            if (!VendorShopResolver.TryGetGameGrandCompanyIdFromSealCurrencyItemId(cost.CurrencyItemId, out var candidate))
                continue;
            if (found && candidate != companyId)
                return false;
            companyId = candidate;
            found = true;
        }

        if (!found
            && entry.CurrencyCostVector is null
            && VendorShopResolver.TryGetGameGrandCompanyIdFromSealCurrencyItemId(entry.CurrencyItemId, out var legacyCompanyId))
        {
            companyId = legacyCompanyId;
            found = true;
        }

        return found;
    }

    public static bool TrySelectAvailableVendor(
        VendorShopEntry entry,
        IEnumerable<VendorNpc> vendors,
        out VendorNpc? selectedVendor,
        out VendorAvailability failure)
        => TrySelectAvailableVendor(entry, vendors, new LiveVendorAvailabilityQueries(), out selectedVendor, out failure);

    public static bool TrySelectAvailableVendor(
        VendorShopEntry entry,
        IEnumerable<VendorNpc> vendors,
        IVendorAvailabilityQueries queries,
        out VendorNpc? selectedVendor,
        out VendorAvailability failure)
    {
        selectedVendor = null;
        failure = new(VendorAvailabilityState.Unknown, "No vendor route is available to evaluate.");
        var hasFailure = false;
        foreach (var vendor in vendors)
        {
            var availability = Resolve(entry, vendor, queries);
            if (availability.IsAvailable)
            {
                selectedVendor = vendor;
                failure = availability;
                return true;
            }

            if (!hasFailure)
            {
                failure = availability;
                hasFailure = true;
            }
        }

        return false;
    }

    private static VendorAvailability ResolveCheck(
        VendorAvailabilityCheck check,
        string unknownReason,
        string lockedPrefix)
    {
        if (!check.IsKnown)
            return new(VendorAvailabilityState.Unknown,
                string.IsNullOrWhiteSpace(check.Description) ? unknownReason : check.Description);
        if (check.IsSatisfied)
            return new(VendorAvailabilityState.Available, string.Empty);

        var description = string.IsNullOrWhiteSpace(check.Description)
            ? unknownReason
            : check.Description;
        return new(VendorAvailabilityState.Locked, $"{lockedPrefix}: {description}.");
    }

    private sealed class LiveVendorAvailabilityQueries : IVendorAvailabilityQueries
    {
        public VendorAvailabilityCheck Quest(uint questId)
        {
            var sheet = Dalamud.GameData.GetExcelSheet<Quest>();
            if (sheet == null || !sheet.TryGetRow(questId, out var quest))
                return new(false, false, $"Required quest #{questId} could not be resolved.");

            return new(true, Dalamud.UnlockState.IsQuestCompleted(quest), quest.Name.ExtractText());
        }

        public VendorAvailabilityCheck Achievement(uint achievementId)
        {
            if (!Dalamud.UnlockState.IsAchievementListLoaded)
                return new(false, false, "Achievement unlock data is not loaded yet.");

            var sheet = Dalamud.GameData.GetExcelSheet<Lumina.Excel.Sheets.Achievement>();
            if (sheet == null || !sheet.TryGetRow(achievementId, out var achievement))
                return new(false, false, $"Required achievement #{achievementId} could not be resolved.");

            return new(true, Dalamud.UnlockState.IsAchievementComplete(achievement), achievement.Name.ExtractText());
        }

        public VendorAvailabilityCheck Content(uint contentId, bool mustBeComplete)
        {
            // RequiredContentFinderCondition stores a ContentFinderCondition
            // row. Resolve its linked InstanceContent explicitly; row IDs are
            // not interchangeable.
            var conditionSheet = Dalamud.GameData.GetExcelSheet<ContentFinderCondition>();
            if (conditionSheet == null || !conditionSheet.TryGetRow(contentId, out var condition))
                return new(false, false, $"Required content #{contentId} could not be resolved safely.");
            var instanceSheet = Dalamud.GameData.GetExcelSheet<Lumina.Excel.Sheets.InstanceContent>();
            var instanceContentId = condition.Content.RowId;
            if (instanceSheet == null || instanceContentId == 0 || !instanceSheet.TryGetRow(instanceContentId, out var content))
                return new(false, false, $"Required content #{contentId} could not be resolved safely.");
            if (mustBeComplete)
            {
                // IUnlockState exposes unlock/access for InstanceContent, but
                // no verified completion query for this relation. Do not
                // treat access as completion.
                return new(false, false, $"Completion of content #{contentId} cannot be verified safely.");
            }

            return new(true, Dalamud.UnlockState.IsInstanceContentUnlocked(content), $"content #{contentId}");
        }

        public unsafe VendorAvailabilityCheck GrandCompany(uint companyId, uint requiredRank)
        {
            if (!VendorShopResolver.TryGetCurrentGrandCompanyId(out var currentCompanyId))
                return new(false, false, "Grand Company membership is not available yet.");

            if (currentCompanyId != companyId)
                return new(true, false, $"membership in {GetGrandCompanyName(companyId)}");

            var playerState = PlayerState.Instance();
            if (playerState == null)
                return new(false, false, "Grand Company rank is not available yet.");
            if (playerState->GetGrandCompanyRank() < requiredRank)
                return new(true, false, $"Grand Company rank {requiredRank}");

            return new(true, true, string.Empty);
        }

        public unsafe VendorAvailabilityCheck AlliedSociety(uint societyId, uint requiredRank)
        {
            var tribeSheet = Dalamud.GameData.GetExcelSheet<Lumina.Excel.Sheets.BeastTribe>();
            if (tribeSheet == null || !tribeSheet.TryGetRow(societyId, out var tribe))
                return new(false, false, $"Allied society #{societyId} could not be resolved.");

            var playerState = PlayerState.Instance();
            if (playerState == null)
                return new(false, false, "Allied-society rank is not available yet.");

            var rank = playerState->GetBeastTribeRank((byte)societyId);
            return new(true, rank >= requiredRank, $"{tribe.Name.ExtractText()} rank {requiredRank}");
        }

        private static string GetGrandCompanyName(uint companyId)
            => companyId switch
            {
                0 => "Maelstrom",
                1 => "Twin Adder",
                2 => "Immortal Flames",
                _ => $"Grand Company #{companyId}",
            };
    }
}
