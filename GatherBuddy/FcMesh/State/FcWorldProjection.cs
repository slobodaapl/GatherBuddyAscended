using System;
using System.Collections.Generic;
using System.Linq;
using GatherBuddy.FcMesh.Fulfillment;
using GatherBuddy.FcMesh.Protocol;
using GatherBuddy.FcMesh.Publication;

namespace GatherBuddy.FcMesh.State;

public sealed record FcChestProjection(
    ChestSnapshotRecord? Snapshot,
    bool IsFresh)
{
    public FcItemQuantityMap Items => Snapshot?.ItemMap ?? FcItemQuantityMap.Empty;
    public CrystalQuantityMap Crystals => Snapshot?.Crystals ?? new CrystalQuantityMap(Array.Empty<CrystalQuantityEntry>());
}

public sealed record FcChestLocationProjection(
    FcEstateChestLocationRecord? Location,
    bool IsKnown,
    bool IsForked)
{
    public bool HasUsableLocation => IsKnown && Location is { Published: true };
}

public sealed class FcProjectedWorld
{
    public FcProjectedWorld(
        FcWorldRevision revision,
        IReadOnlyList<PublishedListRecord> activeLists,
        IReadOnlyList<Guid> activeListIds,
        IReadOnlyList<WorkerSessionRecord> activeWorkers,
        FcChestProjection chest,
        FcChestLocationProjection chestLocation,
        FcFulfillmentMatchResult fulfillment)
    {
        Revision = revision;
        ActiveLists = activeLists;
        ActiveListIds = activeListIds;
        ActiveWorkers = activeWorkers;
        Chest = chest;
        ChestLocation = chestLocation;
        Fulfillment = fulfillment;
    }

    public FcWorldRevision Revision { get; }
    public IReadOnlyList<PublishedListRecord> ActiveLists { get; }
    public IReadOnlyList<Guid> ActiveListIds { get; }
    public IReadOnlyList<WorkerSessionRecord> ActiveWorkers { get; }
    public FcChestProjection Chest { get; }
    public FcChestLocationProjection ChestLocation { get; }
    public FcFulfillmentMatchResult Fulfillment { get; }
}

public sealed class FcWorldProjection
{
    private readonly FcWorldStore _store;
    private readonly FcLivenessOptions _livenessOptions;
    private readonly FcLivenessTracker _liveness;

    public FcWorldProjection(FcWorldStore store, FcLivenessOptions? livenessOptions = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _livenessOptions = livenessOptions ?? FcLivenessOptions.Default;
        _liveness = new FcLivenessTracker(_livenessOptions);
    }

    public FcProjectedWorld Build(IFcClock clock, TimeSpan? chestTtl = null)
        => Build(clock, FcCompatibilityContext.Unavailable, chestTtl);

    public FcProjectedWorld Build(
        IFcClock clock,
        FcCompatibilityContext compatibility,
        TimeSpan? chestTtl = null)
    {
        if (clock is null)
            throw new ArgumentNullException(nameof(clock));
        if (compatibility is null)
            throw new ArgumentNullException(nameof(compatibility));
        var now = clock.UnixMilliseconds;
        var workers = _store.Workers.Values
            .Where(worker => !_store.IsForked(WorkerKey(worker)))
            .Where(worker => _store.GetWorkerHlc(worker.Header.OwnerAuthorId) is { } hlc
                && _liveness.IsActive(worker, hlc, now))
            .OrderBy(worker => worker.Header.OwnerAuthorId, StringComparer.Ordinal)
            .ToArray();

        var publicLists = _store.Lists.Values
            .Where(list => list.Published)
            .Where(list => !_store.IsForked(ListKey(list)))
            .Where(list => compatibility.IsValid
                && list.PlannerSemanticsVersion == compatibility.PlannerSemanticsVersion
                && string.Equals(list.GameVersion, compatibility.GameVersion, StringComparison.Ordinal))
            .OrderBy(list => list.ListId)
            .ThenBy(list => list.Header.OwnerAuthorId, StringComparer.Ordinal)
            .ToArray();
        var publishedById = publicLists
            .GroupBy(list => list.ListId)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(list => list.Header.OwnerAuthorId, StringComparer.Ordinal).First());

        var activeIds = new HashSet<Guid>();
        foreach (var worker in workers)
        {
            if (worker.Selection.AllPublishedLists)
            {
                foreach (var list in publicLists)
                    activeIds.Add(list.ListId);
            }
            else
            {
                foreach (var listId in worker.Selection.ListIds)
                {
                    if (publishedById.ContainsKey(listId))
                        activeIds.Add(listId);
                }
            }
        }

        var activeLists = activeIds
            .OrderBy(id => id)
            .Select(id => publishedById[id])
            .ToArray();
        var chest = ProjectChest(now, chestTtl ?? TimeSpan.FromMinutes(5));
        var chestLocation = ProjectChestLocation(compatibility);
        var demands = activeLists
            .SelectMany(list => list.FinalTargets.Select(target => new FcListDemand(
                list.ListId,
                target.ItemId,
                target.Quality,
                target.Quantity)))
            .ToArray();
        var workerSupplies = workers
            .Select(worker => new FcWorkerSupply(
                worker.Header.OwnerAuthorId,
                worker.HeldInventoryMap,
                EligibleLists(worker, activeIds)))
            .ToArray();
        var fulfillment = FcFulfillmentMatcher.Match(demands, chest.Items, workerSupplies);
        return new FcProjectedWorld(
            _store.Revision,
            activeLists,
            activeIds.OrderBy(id => id).ToArray(),
            workers,
            chest,
            chestLocation,
            fulfillment);
    }

    /// <summary>
    /// Overlays a complete framework-thread chest reread before its native
    /// publication echo arrives. This closes the stale-read window without
    /// treating the local snapshot as a replicated record; the next native
    /// world revision remains authoritative for all peers.
    /// </summary>
    public FcProjectedWorld OverlayChestSnapshot(
        FcProjectedWorld projected,
        ChestSnapshotRecord snapshot)
    {
        if (projected is null)
            throw new ArgumentNullException(nameof(projected));
        if (snapshot is null || !snapshot.Complete)
            throw new ArgumentException("Only complete chest snapshots can be overlaid.", nameof(snapshot));

        var activeIds = projected.ActiveListIds.ToHashSet();
        var demands = projected.ActiveLists
            .SelectMany(list => list.FinalTargets.Select(target => new FcListDemand(
                list.ListId,
                target.ItemId,
                target.Quality,
                target.Quantity)))
            .ToArray();
        var supplies = projected.ActiveWorkers
            .Select(worker => new FcWorkerSupply(
                worker.Header.OwnerAuthorId,
                worker.HeldInventoryMap,
                EligibleLists(worker, activeIds)))
            .ToArray();
        return new FcProjectedWorld(
            projected.Revision,
            projected.ActiveLists,
            projected.ActiveListIds,
            projected.ActiveWorkers,
            new FcChestProjection(snapshot, true),
            projected.ChestLocation,
            FcFulfillmentMatcher.Match(demands, snapshot.ItemMap, supplies));
    }

    public static bool IsCapabilityValid(
        CapabilityRequestRecord request,
        CapabilityResponseRecord response,
        string worldFingerprint,
        IFcClock clock)
    {
        if (request is null || response is null || request.Header is null || response.Header is null || clock is null
            || request.RequestId != response.RequestId
            || request.Header.RecordId != request.RequestId
            || response.Header.RecordId != response.RequestId
            || !string.Equals(request.Header.RecordType, FcRecordTypes.CapabilityRequest, StringComparison.Ordinal)
            || !string.Equals(response.Header.RecordType, FcRecordTypes.CapabilityResponse, StringComparison.Ordinal)
            || !string.Equals(request.WorldFingerprint, worldFingerprint, StringComparison.Ordinal)
            || !string.Equals(request.WorldFingerprint, response.WorldFingerprint, StringComparison.Ordinal)
            || request.ExpiresAt.PhysicalUnixMs < 0
            || response.ExpiresAt.PhysicalUnixMs < 0
            || string.IsNullOrWhiteSpace(request.ExpiresAt.NodeId)
            || string.IsNullOrWhiteSpace(response.ExpiresAt.NodeId)
            || response.ExpiresAt.PhysicalUnixMs <= clock.UnixMilliseconds
            || request.ExpiresAt.PhysicalUnixMs <= clock.UnixMilliseconds
            || string.IsNullOrWhiteSpace(response.GameVersion)
            || string.IsNullOrWhiteSpace(response.PlannerFingerprint)
            || string.IsNullOrWhiteSpace(response.GearsetFingerprint)
            || string.IsNullOrWhiteSpace(response.SolverFingerprint)
            || request.Recipes is null || response.Results is null)
            return false;

        if (request.Recipes.Any(recipe => recipe is null
                || recipe.QualityPolicy is null
                || recipe.QualityPolicy.Rules is null
                || recipe.FinalQualityPolicy is null
                || recipe.PrecraftQualityPolicy is null)
            || response.Results.Any(result => result is null)
            || request.Recipes.Select(recipe => recipe.RecipeId).Distinct().Count() != request.Recipes.Length
            || response.Results.Select(result => result.RecipeId).Distinct().Count() != response.Results.Length)
            return false;
        var requested = request.Recipes.ToDictionary(recipe => recipe.RecipeId);
        if (requested.Count != response.Results.Length)
            return false;
        if (!string.IsNullOrWhiteSpace(request.GameVersion)
            && !string.Equals(request.GameVersion, response.GameVersion, StringComparison.Ordinal))
            return false;
        if (!string.IsNullOrWhiteSpace(request.PlannerFingerprint)
            && !string.Equals(request.PlannerFingerprint, response.PlannerFingerprint, StringComparison.Ordinal))
            return false;
        if (response.RequestedRecipes is not null
            && !HasExactCapabilitySet(request.Recipes, response.RequestedRecipes))
            return false;
        foreach (var result in response.Results)
        {
            if (!requested.TryGetValue(result.RecipeId, out var recipe)
                || !result.CanCraft
                || result.SelectedJobId is null
                || result.SelectedJobId == 0)
                return false;
            var requiresHq = recipe.EffectiveQualityPolicy.Rules.Any(rule => rule.Quality == FcItemQuality.Hq);
            if (requiresHq
                && (!result.GuaranteesRequiredQuality
                    || result.Assessment != FcRaphaelAssessmentOutcome.FullQuality))
                return false;
        }
        return true;
    }

    public static bool IsCapabilityValid(
        CapabilityRequestRecord request,
        CapabilityResponseRecord response,
        WorkerSessionRecord responder,
        FcHlcTimestamp responderHlc,
        string worldFingerprint,
        IFcClock clock,
        FcLivenessOptions? options = null)
    {
        if (request is null || response is null || request.Header is null || response.Header is null
            || responder is null || responder.Header is null
            || clock is null)
            return false;
        var selected = options ?? FcLivenessOptions.Default;
        if (responderHlc.PhysicalUnixMs < 0 || string.IsNullOrWhiteSpace(responderHlc.NodeId))
            return false;
        var age = clock.UnixMilliseconds - responderHlc.PhysicalUnixMs;
        if (response.SessionId != Guid.Empty
            && (response.SessionId != responder.SessionId
                || response.SessionGeneration != responder.SessionGeneration))
            return false;
        return responder.State is FcWorkerState.Active or FcWorkerState.Waiting
            && string.Equals(response.Header.OwnerAuthorId, responder.Header.OwnerAuthorId, StringComparison.Ordinal)
            && age < selected.Horizon.TotalMilliseconds
            && age >= -selected.MaxClockDelta.TotalMilliseconds
            && IsCapabilityValid(request, response, worldFingerprint, clock);
    }

    private static bool HasExactCapabilitySet(
        IReadOnlyList<RequiredCraftCapability> left,
        IReadOnlyList<RequiredCraftCapability> right)
    {
        if (left.Count != right.Count)
            return false;
        var leftById = left.ToDictionary(recipe => recipe.RecipeId);
        foreach (var recipe in right)
        {
            if (!leftById.TryGetValue(recipe.RecipeId, out var expected)
                || expected.IsPrecraft != recipe.IsPrecraft
                || expected.EffectiveQualityPolicy is null
                || recipe.EffectiveQualityPolicy is null
                || FcCanonical.Hash(expected.EffectiveQualityPolicy)
                    != FcCanonical.Hash(recipe.EffectiveQualityPolicy))
                return false;
        }
        return true;
    }

    private FcChestProjection ProjectChest(long now, TimeSpan ttl)
    {
        var ttlMs = checked((long)ttl.TotalMilliseconds);
        var maxDeltaMs = checked((long)_livenessOptions.MaxClockDelta.TotalMilliseconds);
        var snapshot = _store.Chests.Values
            .Where(chest => chest.Complete)
            .Where(chest => !_store.IsForked(ChestKey(chest)))
            .Where(chest =>
            {
                var hlc = _store.GetChestHlc(chest.Header.OwnerAuthorId);
                if (hlc is null)
                    return false;
                var age = now - hlc.Value.PhysicalUnixMs;
                return age < ttlMs && age >= -maxDeltaMs;
            })
            .OrderBy(chest => _store.GetChestHlc(chest.Header.OwnerAuthorId)!.Value)
            .ThenBy(chest => chest.Header.OwnerAuthorId, StringComparer.Ordinal)
            .LastOrDefault();
        return new FcChestProjection(snapshot, snapshot is not null);
    }

    private FcChestLocationProjection ProjectChestLocation(FcCompatibilityContext compatibility)
    {
        var candidates = _store.ChestLocations.Values
            .Where(location => !_store.IsForked(ChestLocationKey(location)))
            .Where(location => location.Published)
            .Where(location => compatibility.IsValid
                && string.Equals(location.CompatibilityFingerprint, compatibility.GameVersion, StringComparison.Ordinal))
            .Select(location => (Location: location, Hlc: _store.GetChestLocationHlc(location.Header.OwnerAuthorId)))
            .Where(value => value.Hlc is not null)
            .OrderBy(value => value.Hlc!.Value)
            .ThenBy(value => value.Location.Header.OwnerAuthorId, StringComparer.Ordinal)
            .LastOrDefault();
        var forked = _store.ChestLocations.Values.Any(location => _store.IsForked(ChestLocationKey(location)));
        return new FcChestLocationProjection(candidates.Location, candidates.Location is not null, forked);
    }

    private static IReadOnlySet<Guid> EligibleLists(WorkerSessionRecord worker, IReadOnlySet<Guid> activeIds)
        => worker.Selection.AllPublishedLists
            ? activeIds
            : worker.Selection.ListIds.Where(activeIds.Contains).ToHashSet();

    private static string ListKey(PublishedListRecord value)
        => value.Header.OwnerAuthorId + "/list/" + value.ListId.ToString("D");

    private static string WorkerKey(WorkerSessionRecord value)
        => value.Header.OwnerAuthorId + "/worker";

    private static string ChestKey(ChestSnapshotRecord value)
        => value.Header.OwnerAuthorId + "/chest";

    private static string ChestLocationKey(FcEstateChestLocationRecord value)
        => value.Header.OwnerAuthorId + "/fc-chest-location";
}
