using System;
using System.Collections.Generic;
using System.Linq;

namespace GatherBuddy.Crafting.Acquisition;

public static class AcquisitionWorldGateways
{
    public const uint Gridania = 2;
    public const uint LimsaLominsa = 8;
    public const uint Uldah = 9;

    public static readonly IReadOnlyList<uint> Preferred = new[] { Gridania, LimsaLominsa, Uldah };

    public static string GetName(uint gatewayId)
        => gatewayId switch
        {
            Gridania => "Gridania",
            LimsaLominsa => "Limsa Lominsa",
            Uldah => "Ul'dah",
            _ => string.Empty,
        };
}

public sealed record AcquisitionWorldRoute(
    uint WorldId,
    string WorldName,
    uint GatewayId,
    string GatewayName,
    bool IsWorldHop,
    bool IsCurrentWorld,
    long TeleportCost = 0);

public sealed record AcquisitionRoutePlan(
    bool IsReady,
    IReadOnlyList<AcquisitionWorldRoute> Routes,
    string FailureReason = "");

public sealed class AcquisitionRouteInput
{
    public uint CurrentWorldId { get; init; }
    public string CurrentWorldName { get; init; } = string.Empty;
    public bool CurrentWorldOnly { get; init; }
    public bool LifestreamAvailable { get; init; }
    public bool NonCrossWorldParty { get; init; }
    public bool TravelProhibited { get; init; }
    public uint CurrentGatewayId { get; init; }
    public Func<uint, bool> CanVisitWorld { get; init; } = _ => true;
    public Func<uint, bool> IsGatewayAttuned { get; init; } = _ => true;
    public Func<uint, long> GatewayTeleportCost { get; init; } = _ => long.MaxValue;
    public Func<uint, string> ResolveWorldName { get; init; } = worldId => worldId.ToString();
}

/// <summary>
/// Pure route selection for a complete acquisition plan. Market transactions
/// are grouped by world before execution; vendor transactions do not require a
/// market-board route. No cross-DC decision is made here: CanVisitWorld is the
/// injected same-DC capability check.
/// </summary>
public static class AcquisitionRoutePlanner
{
    public static AcquisitionRoutePlan Plan(
        AcquisitionPlan plan,
        AcquisitionRouteInput input)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(input);

        if (plan.Transactions.Any(transaction => transaction.SourceKind == AcquisitionSourceKind.Market
                && transaction.WorldId == 0))
            return new AcquisitionRoutePlan(false, Array.Empty<AcquisitionWorldRoute>(), "A market transaction has no resolved world.");

        var groups = plan.Transactions
            .Where(transaction => transaction.SourceKind == AcquisitionSourceKind.Market)
            .GroupBy(transaction => transaction.WorldId)
            .Select(group => new
            {
                WorldId = group.Key,
                WorldName = group.FirstOrDefault(transaction => !string.IsNullOrWhiteSpace(transaction.WorldName))?.WorldName
                    ?? input.ResolveWorldName(group.Key),
                // Market transaction GilCost already includes its listing tax;
                // adding TaxGilCost again would distort world ordering.
                Cost = group.Sum(transaction => transaction.GilCost),
            })
            // Already being on the current world is a zero-Gil route. Use it
            // before any world hop; remaining worlds are then cheapest first.
            .OrderByDescending(group => group.WorldId == input.CurrentWorldId || group.WorldId == 0)
            .ThenBy(group => group.Cost)
            .ThenBy(group => group.WorldId)
            .ToArray();

        if (groups.Length == 0)
            return new AcquisitionRoutePlan(true, Array.Empty<AcquisitionWorldRoute>());
        if (input.CurrentWorldId == 0)
            return new AcquisitionRoutePlan(false, Array.Empty<AcquisitionWorldRoute>(), "The current world is unknown; marketplace routing cannot start.");

        if (input.CurrentWorldOnly && groups.Any(group => group.WorldId != 0 && group.WorldId != input.CurrentWorldId))
        {
            return new AcquisitionRoutePlan(
                false,
                Array.Empty<AcquisitionWorldRoute>(),
                "Marketplace acquisition requires another world while Current world only is enabled.");
        }

        var needsWorldHop = groups.Any(group => group.WorldId != 0 && group.WorldId != input.CurrentWorldId);
        if (needsWorldHop)
        {
            if (!input.LifestreamAvailable)
                return new AcquisitionRoutePlan(false, Array.Empty<AcquisitionWorldRoute>(), "Lifestream is required for marketplace world travel.");
            if (input.NonCrossWorldParty)
                return new AcquisitionRoutePlan(false, Array.Empty<AcquisitionWorldRoute>(), "World travel is unavailable while in a non-cross-world party.");
            if (input.TravelProhibited)
                return new AcquisitionRoutePlan(false, Array.Empty<AcquisitionWorldRoute>(), "The current duty or travel state blocks world travel.");
        }

        var routes = new List<AcquisitionWorldRoute>(groups.Length);
        foreach (var group in groups)
        {
            var isCurrent = group.WorldId == 0 || group.WorldId == input.CurrentWorldId;
            var worldId = group.WorldId == 0 ? input.CurrentWorldId : group.WorldId;
            var worldName = string.IsNullOrWhiteSpace(group.WorldName)
                ? input.CurrentWorldName
                : group.WorldName;

            if (!isCurrent && !input.CanVisitWorld(worldId))
                return new AcquisitionRoutePlan(false, Array.Empty<AcquisitionWorldRoute>(), $"World {worldName} is not reachable in the current data center.");

            var gatewayId = 0u;
            var gatewayCost = 0L;
            if (!isCurrent)
            {
                var gateway = AcquisitionWorldGateways.Preferred
                    .Select((id, preference) =>
                    {
                        var isCurrent = id == input.CurrentGatewayId;
                        return new
                        {
                            Id = id,
                            Preference = preference,
                            IsCurrent = isCurrent,
                            Cost = isCurrent ? 0 : input.GatewayTeleportCost(id),
                        };
                    })
                    .Where(candidate => input.IsGatewayAttuned(candidate.Id)
                        && candidate.Cost >= 0
                        && candidate.Cost != long.MaxValue)
                    .OrderByDescending(candidate => candidate.IsCurrent)
                    .ThenBy(candidate => candidate.Cost)
                    .ThenBy(candidate => candidate.Preference)
                    .FirstOrDefault();
                if (gateway == null)
                {
                    return new AcquisitionRoutePlan(
                        false,
                        Array.Empty<AcquisitionWorldRoute>(),
                        $"No attuned Gridania, Limsa Lominsa, or Ul'dah world-travel gateway is available for {worldName}.");
                }

                gatewayId = gateway.Id;
                gatewayCost = gateway.Cost;
            }

            routes.Add(new AcquisitionWorldRoute(
                worldId,
                worldName,
                gatewayId,
                AcquisitionWorldGateways.GetName(gatewayId),
                !isCurrent,
                isCurrent,
                gatewayCost));
        }

        return new AcquisitionRoutePlan(true, routes);
    }
}
