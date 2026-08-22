using System;
using SeHousingManager = FFXIVClientStructs.FFXIV.Client.Game.HousingManager;
using SeEstateType = FFXIVClientStructs.FFXIV.Client.Game.EstateType;

namespace GatherBuddy.SeFunctions;

public static unsafe class HousingManager
{
    public static bool IsInHousing()
    {
        var housingManager = SeHousingManager.Instance();
        if (housingManager == null)
            return false;

        ref var housingTerritory = ref housingManager->CurrentTerritory;
        if (housingTerritory == null)
            return false;
        
        return true;
    }

    /// <summary>
    /// Returns the native housing identity needed to prove that the current
    /// indoor plot is the character's owned Free Company estate. The native
    /// HouseId value is compared exactly; territory-only or plot-only matches
    /// are not accepted as ownership evidence.
    /// </summary>
    public static bool TryGetCurrentFreeCompanyEstate(
        out ulong currentIndoorHouseId,
        out ulong ownedFreeCompanyHouseId,
        out uint originalHouseTerritoryTypeId,
        out byte currentDivision,
        out string error)
    {
        currentIndoorHouseId = 0;
        ownedFreeCompanyHouseId = 0;
        originalHouseTerritoryTypeId = 0;
        currentDivision = 0;
        var housingManager = SeHousingManager.Instance();
        if (housingManager == null)
        {
            error = "Native HousingManager is unavailable.";
            return false;
        }
        if (!housingManager->IsInside())
        {
            error = "Character is not inside a housing interior.";
            return false;
        }

        var current = housingManager->GetCurrentIndoorHouseId();
        var owned = SeHousingManager.GetOwnedHouseId(SeEstateType.FreeCompanyEstate);
        var originalTerritory = SeHousingManager.GetOriginalHouseTerritoryTypeId();
        var division = housingManager->GetCurrentDivision();
        currentIndoorHouseId = current.Id;
        ownedFreeCompanyHouseId = owned.Id;
        originalHouseTerritoryTypeId = originalTerritory;
        currentDivision = division;
        if (current.Id == 0 || owned.Id == 0 || originalTerritory == 0 || division is < 1 or > 2)
        {
            error = "Current indoor/owned Free Company estate identity or housing division is unavailable.";
            return false;
        }
        if (current.Id != owned.Id)
        {
            error = "Current indoor house identity does not match the owned Free Company estate.";
            return false;
        }
        error = string.Empty;
        return true;
    }
}
