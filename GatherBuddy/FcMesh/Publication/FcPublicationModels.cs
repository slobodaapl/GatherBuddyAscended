using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using GatherBuddy.Crafting;
using GatherBuddy.FcMesh.Protocol;
using GatherBuddy.FcMesh.State;

namespace GatherBuddy.FcMesh.Publication;

/// <summary>
/// Durable local identity. The integer list ID is reusable, so CreatedAt is
/// part of the identity and mappings are never inferred from ID alone.
/// </summary>
public readonly record struct FcLocalListIdentity(int ListId, DateTime CreatedAt)
{
    public DateTime CreatedAtUtc => CreatedAt.ToUniversalTime();

    public string StorageKey
        => $"{ListId}:{CreatedAtUtc.Ticks}";
}

public sealed record FcRecipeOutput(uint ItemId, uint Amount, bool CanBeHq = true);

public sealed record FcPublishedListMapResult(
    bool IsValid,
    string Error,
    PublishedListRecord? Record)
{
    public static FcPublishedListMapResult Invalid(string error)
        => new(false, error, null);

    public static FcPublishedListMapResult Valid(PublishedListRecord record)
        => new(true, string.Empty, record);
}

public static class FcPublishedListMapper
{
    public const int CurrentPlannerSemanticsVersion = 1;

    public static FcPublishedListMapResult TryMap(
        CraftingListDefinition list,
        string ownerAuthorId,
        string gameVersion,
        ulong revision,
        Guid publicListId,
        Func<uint, FcRecipeOutput?> recipeLookup)
    {
        if (list is null)
            return FcPublishedListMapResult.Invalid("Crafting list is unavailable.");
        if (publicListId == Guid.Empty)
            return FcPublishedListMapResult.Invalid("Public list identity is empty.");
        if (revision == 0)
            return FcPublishedListMapResult.Invalid("Published list revision is invalid.");
        if (string.IsNullOrWhiteSpace(ownerAuthorId))
            return FcPublishedListMapResult.Invalid("Character author is unavailable.");
        if (string.IsNullOrWhiteSpace(gameVersion))
            return FcPublishedListMapResult.Invalid("Game compatibility version is unavailable.");
        if (string.IsNullOrWhiteSpace(list.Name))
            return FcPublishedListMapResult.Invalid("Published list name is empty.");
        if (recipeLookup is null)
            return FcPublishedListMapResult.Invalid("Recipe lookup is unavailable.");
        if (list.Recipes is null || list.Recipes.Count == 0)
            return FcPublishedListMapResult.Invalid("Published list has no final recipes.");

        var targets = new List<PublishedRecipeTarget>(list.Recipes.Count);
        var recipeIds = new HashSet<uint>();
        var itemKeys = new HashSet<(uint ItemId, FcItemQuality Quality)>();
        foreach (var item in list.Recipes)
        {
            if (item is null || item.Options is null)
                return FcPublishedListMapResult.Invalid("Published list contains an incomplete recipe entry.");
            if (item.Options.Skipping)
                continue;
            if (item.RecipeId == 0 || item.Quantity <= 0)
                return FcPublishedListMapResult.Invalid("Final recipe quantities must be positive.");

            var output = recipeLookup(item.RecipeId);
            if (output is null || output.ItemId == 0 || output.Amount == 0)
                return FcPublishedListMapResult.Invalid($"Final recipe {item.RecipeId} could not be resolved.");
            int quantity;
            try
            {
                quantity = checked(item.Quantity * checked((int)output.Amount));
            }
            catch (OverflowException)
            {
                return FcPublishedListMapResult.Invalid($"Final recipe {item.RecipeId} quantity overflows the protocol range.");
            }

            var quality = item.Options.NQOnly || !output.CanBeHq
                ? FcItemQuality.Nq
                : FcItemQuality.Hq;
            if (!recipeIds.Add(item.RecipeId)
                || !itemKeys.Add((output.ItemId, quality)))
                return FcPublishedListMapResult.Invalid("Final recipes and quality-keyed output items must be unique.");
            targets.Add(new PublishedRecipeTarget(item.RecipeId, output.ItemId, quantity, quality));
        }

        if (targets.Count == 0)
            return FcPublishedListMapResult.Invalid("Published list has no active final recipes.");

        var orderedTargets = targets
            .OrderBy(target => target.ItemId)
            .ThenBy(target => target.Quality)
            .ThenBy(target => target.RecipeId)
            .ToArray();
        var qualityRules = orderedTargets
            .Select(target => new FcQualityRule(target.ItemId, target.Quality, target.Quantity))
            .ToArray();
        var header = new FcRecordHeader(
            FcProtocolVersion.Current,
            FcProtocolVersion.CurrentSchema,
            FcRecordTypes.PublishedList,
            publicListId,
            ownerAuthorId,
            revision);
        return FcPublishedListMapResult.Valid(new PublishedListRecord(
            header,
            publicListId,
            list.Name,
            true,
            CurrentPlannerSemanticsVersion,
            gameVersion,
            orderedTargets,
            new FcQualityPolicy(qualityRules),
            FcQualityPolicy.Empty));
    }
}

public sealed record FcCompatibilityContext(int PlannerSemanticsVersion, string GameVersion)
{
    public static FcCompatibilityContext Unavailable { get; } = new(-1, string.Empty);

    public bool IsValid
        => PlannerSemanticsVersion >= 0 && !string.IsNullOrWhiteSpace(GameVersion);
}

public sealed record FcListCompatibilityResult(bool IsCompatible, string Reason)
{
    public static FcListCompatibilityResult Compatible { get; } = new(true, string.Empty);
}

public static class FcPublishedListCompatibility
{
    public static FcListCompatibilityResult Evaluate(
        PublishedListRecord? record,
        FcCompatibilityContext context)
    {
        if (record is null)
            return new(false, "Published list record is unavailable.");
        if (!context.IsValid)
            return new(false, "Local planner/game compatibility is unavailable.");
        if (record.PlannerSemanticsVersion != context.PlannerSemanticsVersion)
            return new(false, $"Planner semantics {record.PlannerSemanticsVersion} is not supported locally.");
        if (!string.Equals(record.GameVersion, context.GameVersion, StringComparison.Ordinal))
            return new(false, $"Game version {record.GameVersion} does not match local {context.GameVersion}.");
        return FcListCompatibilityResult.Compatible;
    }
}

public sealed record FcPublicListView(
    PublishedListRecord Record,
    bool IsCompatible,
    string CompatibilityReason,
    bool IsOrphanedLocalMapping)
{
    public string OwnerAuthorId => Record.Header.OwnerAuthorId;
    public ulong Revision => Record.Header.Revision;
    public bool Published => Record.Published;
}

public enum FcPublicationCommandKind : byte
{
    Publish,
    Update,
    Unpublish,
    ChestObservation,
}

public enum FcPublicationCommandStatus : byte
{
    None,
    Pending,
    AwaitingFreshObservation,
    AcceptedByNative,
    Failed,
    Blocked,
}

public sealed record FcPublicationCommandState(
    FcPublicationCommandKind Kind,
    ulong Revision,
    string PayloadHash,
    FcPublicationCommandStatus Status,
    string Error);

public sealed record FcLocalPublishedListState(
    int LocalListId,
    DateTime CreatedAtUtc,
    Guid PublicListId,
    ulong ReservedRevision,
    PublishedListRecord? LastPublishedSnapshot,
    string LastPublishedHash,
    FcPublicationCommandState? Pending,
    PublishedListRecord? PendingSnapshot);

public sealed record FcPublicationState(
    int Version,
    string BackendKind,
    int BackendStorageVersion,
    string BackendMarker,
    string AuthorScope,
    FcLocalPublishedListState[] Lists,
    Guid ChestRecordId,
    ulong ChestReservedRevision,
    ChestSnapshotRecord? LastChestSnapshot,
    string LastChestHash,
    FcPublicationCommandState? PendingChest,
    ChestSnapshotRecord? PendingChestSnapshot)
{
    public const int CurrentVersion = 1;
    public const string CurrentBackendKind = "iroh-docs";
    public const int CurrentBackendStorageVersion = 1;
    public const string CurrentInitializationMarker = "gatherbuddy-fcmesh-publication-state-v1";

    public static FcPublicationState Create(string authorScope)
    {
        var scope = authorScope ?? string.Empty;
        var chestIdentity = SHA256.HashData(Encoding.UTF8.GetBytes("gatherbuddy/fcmesh/chest/" + scope));
        return new(
            CurrentVersion,
            CurrentBackendKind,
            CurrentBackendStorageVersion,
            Guid.NewGuid().ToString("N"),
            scope,
            Array.Empty<FcLocalPublishedListState>(),
            new Guid(chestIdentity.AsSpan(0, 16)),
            0,
            null,
            string.Empty,
            null,
            null);
    }
}

public enum FcPublicationStateLoadStatus : byte
{
    Clean,
    Missing,
    Corrupt,
}

public sealed record FcPublicationStateLoadResult(
    FcPublicationStateLoadStatus Status,
    FcPublicationState State,
    string Error)
{
    public bool CanWrite => Status is FcPublicationStateLoadStatus.Clean or FcPublicationStateLoadStatus.Missing;
}

public interface IFcPublicationStateStore
{
    FcPublicationStateLoadResult Load(string authorScope);
    void Save(string authorScope, FcPublicationState state);
}

public sealed class FcInMemoryPublicationStateStore : IFcPublicationStateStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, FcPublicationState> _states = new(StringComparer.Ordinal);
    private readonly HashSet<string> _corruptScopes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _initializedScopes = new(StringComparer.Ordinal);

    public bool FailWrites { get; set; }

    public FcPublicationStateLoadResult Load(string authorScope)
    {
        lock (_gate)
        {
            if (_corruptScopes.Contains(authorScope))
                return new(FcPublicationStateLoadStatus.Corrupt, FcPublicationState.Create(authorScope), "Stored publication state is corrupt.");
            if (!_states.TryGetValue(authorScope, out var state))
            {
                if (_initializedScopes.Contains(authorScope))
                    return new(
                        FcPublicationStateLoadStatus.Corrupt,
                        FcPublicationState.Create(authorScope),
                        "Publication initialization marker exists but state is missing.");
                return new(FcPublicationStateLoadStatus.Missing, FcPublicationState.Create(authorScope), string.Empty);
            }
            if (!_initializedScopes.Contains(authorScope))
                return new(
                    FcPublicationStateLoadStatus.Corrupt,
                    FcPublicationState.Create(authorScope),
                    "Publication state exists without its initialization marker.");
            return new(FcPublicationStateLoadStatus.Clean, FcPublicationStateClone.Clone(state), string.Empty);
        }
    }

    public void Save(string authorScope, FcPublicationState state)
    {
        if (FailWrites)
            throw new IOException("Synthetic publication-state write failure.");
        if (string.IsNullOrWhiteSpace(authorScope)
            || state is null
            || !string.Equals(state.AuthorScope, authorScope, StringComparison.Ordinal)
            || state.Version != FcPublicationState.CurrentVersion
            || !string.Equals(state.BackendKind, FcPublicationState.CurrentBackendKind, StringComparison.Ordinal)
            || state.BackendStorageVersion != FcPublicationState.CurrentBackendStorageVersion)
            throw new InvalidDataException("Synthetic publication state schema or scope is invalid.");
        lock (_gate)
        {
            if (_states.TryGetValue(authorScope, out var current))
                state = FcPublicationStateMerge.Merge(current, state);
            _initializedScopes.Add(authorScope);
            _states[authorScope] = FcPublicationStateClone.Clone(state);
        }
    }

    public void MarkCorrupt(string authorScope)
    {
        lock (_gate)
            _corruptScopes.Add(authorScope);
    }

    /// <summary>Test seam for the crash window after the marker commit and before state commit.</summary>
    public void MarkInitializationMarkerOnly(string authorScope)
    {
        lock (_gate)
            _initializedScopes.Add(authorScope);
    }
}

internal static class FcPublicationStateClone
{
    public static FcPublicationState Clone(FcPublicationState state)
        => new(
            state.Version,
            state.BackendKind,
            state.BackendStorageVersion,
            state.BackendMarker,
            state.AuthorScope,
            state.Lists?.Select(Clone).ToArray() ?? Array.Empty<FcLocalPublishedListState>(),
            state.ChestRecordId,
            state.ChestReservedRevision,
            Clone(state.LastChestSnapshot),
            state.LastChestHash,
            Clone(state.PendingChest),
            Clone(state.PendingChestSnapshot));

    private static FcLocalPublishedListState Clone(FcLocalPublishedListState value)
        => new(
            value.LocalListId,
            value.CreatedAtUtc,
            value.PublicListId,
            value.ReservedRevision,
            Clone(value.LastPublishedSnapshot),
            value.LastPublishedHash,
            Clone(value.Pending),
            Clone(value.PendingSnapshot));

    private static FcPublicationCommandState? Clone(FcPublicationCommandState? value)
        => value is null ? null : value with { };

    private static PublishedListRecord? Clone(PublishedListRecord? value)
        => value is null
            ? null
            : value with
            {
                Header = value.Header with { },
                FinalTargets = (value.FinalTargets ?? Array.Empty<PublishedRecipeTarget>())
                    .Select(target => target with { })
                    .ToArray(),
                FinalQualityPolicy = Clone(value.FinalQualityPolicy),
                PrecraftQualityPolicy = Clone(value.PrecraftQualityPolicy),
            };

    private static FcQualityPolicy Clone(FcQualityPolicy? value)
        => value is null
            ? FcQualityPolicy.Empty
            : new FcQualityPolicy((value.Rules ?? Array.Empty<FcQualityRule>())
                .Select(rule => rule with { })
                .ToArray());

    private static ChestSnapshotRecord? Clone(ChestSnapshotRecord? value)
        => value is null
            ? null
            : value with
            {
                Header = value.Header with { },
                Items = (value.Items ?? Array.Empty<ItemQuantityEntry>())
                    .Select(entry => entry with { })
                    .ToArray(),
                Crystals = new CrystalQuantityMap((value.Crystals?.Entries ?? Array.Empty<CrystalQuantityEntry>())
                    .Select(entry => entry with { })
                    .ToArray()),
            };
}

internal static class FcPublicationStateMerge
{
    public static FcPublicationState Merge(FcPublicationState current, FcPublicationState incoming)
    {
        if (!string.Equals(current.AuthorScope, incoming.AuthorScope, StringComparison.Ordinal)
            || !string.Equals(current.BackendKind, incoming.BackendKind, StringComparison.Ordinal)
            || current.BackendStorageVersion != incoming.BackendStorageVersion
            || !string.Equals(current.BackendMarker, incoming.BackendMarker, StringComparison.Ordinal))
            throw new InvalidDataException("Publication state scope or backend schema does not match.");
        if (current.ChestRecordId != Guid.Empty
            && incoming.ChestRecordId != Guid.Empty
            && current.ChestRecordId != incoming.ChestRecordId)
            throw new InvalidDataException("Publication chest register identity does not match.");
        if (current.ChestReservedRevision == incoming.ChestReservedRevision
            && SnapshotHash(current.LastChestSnapshot, current.PendingChestSnapshot, current.ChestReservedRevision) is { } currentChestHash
            && SnapshotHash(incoming.LastChestSnapshot, incoming.PendingChestSnapshot, incoming.ChestReservedRevision) is { } incomingChestHash
            && !string.Equals(currentChestHash, incomingChestHash, StringComparison.Ordinal))
            throw new InvalidDataException("Publication chest register has an equal-revision fork.");

        var byIdentity = new Dictionary<string, FcLocalPublishedListState>(StringComparer.Ordinal);
        var publicIds = new Dictionary<Guid, string>();
        foreach (var mapping in current.Lists ?? Array.Empty<FcLocalPublishedListState>())
            Add(mapping, byIdentity, publicIds, incoming: false);
        foreach (var mapping in incoming.Lists ?? Array.Empty<FcLocalPublishedListState>())
            Add(mapping, byIdentity, publicIds, incoming: true);

        var chest = current.ChestReservedRevision >= incoming.ChestReservedRevision
            ? current
            : incoming;
        if (current.ChestReservedRevision == incoming.ChestReservedRevision
            && incoming.PendingChest is null
            && current.PendingChest is not null)
            chest = incoming;
        else if (current.ChestReservedRevision == incoming.ChestReservedRevision
            && current.PendingChest is null
            && incoming.PendingChest is not null)
            chest = current;

        return current with
        {
            BackendMarker = current.BackendMarker,
            Lists = byIdentity.Values
                .OrderBy(value => value.LocalListId)
                .ThenBy(value => value.CreatedAtUtc)
                .ToArray(),
            ChestRecordId = current.ChestRecordId != Guid.Empty
                ? current.ChestRecordId
                : chest.ChestRecordId,
            ChestReservedRevision = chest.ChestReservedRevision,
            LastChestSnapshot = chest.LastChestSnapshot,
            LastChestHash = chest.LastChestHash,
            PendingChest = chest.PendingChest,
            PendingChestSnapshot = chest.PendingChestSnapshot,
        };

        void Add(
            FcLocalPublishedListState mapping,
            Dictionary<string, FcLocalPublishedListState> mappings,
            Dictionary<Guid, string> ids,
            bool incoming)
        {
            var identity = new FcLocalListIdentity(mapping.LocalListId, mapping.CreatedAtUtc).StorageKey;
            if (mappings.TryGetValue(identity, out var existing))
            {
                if (existing.PublicListId != mapping.PublicListId)
                    throw new InvalidDataException("Publication list identity changed.");
                if (existing.ReservedRevision == mapping.ReservedRevision
                    && SnapshotHash(existing.LastPublishedSnapshot, existing.PendingSnapshot, existing.ReservedRevision) is { } existingHash
                    && SnapshotHash(mapping.LastPublishedSnapshot, mapping.PendingSnapshot, mapping.ReservedRevision) is { } mappingHash
                    && !string.Equals(existingHash, mappingHash, StringComparison.Ordinal))
                    throw new InvalidDataException("Publication list register has an equal-revision fork.");
                mappings[identity] = SelectMapping(existing, mapping, incoming);
                return;
            }
            if (ids.TryGetValue(mapping.PublicListId, out var existingIdentity)
                && !string.Equals(existingIdentity, identity, StringComparison.Ordinal))
                throw new InvalidDataException("Publication list identity is reused.");
            mappings.Add(identity, mapping);
            ids[mapping.PublicListId] = identity;
        }
    }

    private static string? SnapshotHash<T>(T? published, T? pending, ulong revision)
        where T : class
    {
        var snapshot = pending ?? published;
        return snapshot is not null && GetRevision(snapshot) == revision
            ? FcCanonical.PayloadHash(snapshot)
            : null;
    }

    private static ulong GetRevision(object snapshot)
        => snapshot switch
        {
            PublishedListRecord record => record.Header.Revision,
            ChestSnapshotRecord record => record.Header.Revision,
            _ => 0,
        };

    private static FcLocalPublishedListState SelectMapping(
        FcLocalPublishedListState current,
        FcLocalPublishedListState incoming,
        bool incomingIsLaterSource)
    {
        if (current.ReservedRevision != incoming.ReservedRevision)
            return current.ReservedRevision > incoming.ReservedRevision ? current : incoming;
        if (current.Pending is null && incoming.Pending is not null)
            return current;
        if (incoming.Pending is null && current.Pending is not null)
            return incoming;
        return incomingIsLaterSource ? incoming : current;
    }
}
