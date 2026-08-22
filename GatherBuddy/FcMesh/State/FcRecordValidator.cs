using System;
using System.Collections.Generic;
using System.Linq;
using GatherBuddy.FcMesh.Protocol;

namespace GatherBuddy.FcMesh.State;

public static class FcRecordTypes
{
    public const string GroupMetadata = "group-metadata";
    public const string PublishedList = "published-list";
    public const string WorkerSession = "worker-session";
    public const string ChestSnapshot = "chest-snapshot";
    public const string CapabilityRequest = "capability-request";
    public const string CapabilityResponse = "capability-response";
    public const string InventoryTransfer = "inventory-transfer";
    public const string FcChestLocation = "fc-chest-location";
}

public sealed record FcValidationResult(bool IsValid, string Error)
{
    public static FcValidationResult Valid { get; } = new(true, string.Empty);
    public static FcValidationResult Invalid(string error) => new(false, error);
}

public sealed class FcRecordValidator
{
    public const uint CompleteChestPageMask = 0x3F;
    public const int MaxArrayEntries = 100_000;
    public const int MaxPayloadBytes = 16 * 1024 * 1024;

    private readonly IFcMeshRecordVerifier _verifier;

    public FcRecordValidator(IFcMeshRecordVerifier? verifier = null)
    {
        _verifier = verifier ?? new FcVerifiedContextVerifier();
    }

    public FcValidationResult Validate(object? record, string transportAuthorId)
    {
        if (record is null)
            return FcValidationResult.Invalid("Record is null.");
        if (string.IsNullOrWhiteSpace(transportAuthorId))
            return FcValidationResult.Invalid("Transport/document author is missing.");

        return record switch
        {
            PublishedListRecord value => ValidateList(value, transportAuthorId),
            WorkerSessionRecord value => ValidateWorker(value, transportAuthorId),
            ChestSnapshotRecord value => ValidateChest(value, transportAuthorId),
            CapabilityRequestRecord value => ValidateRequest(value, transportAuthorId),
            CapabilityResponseRecord value => ValidateResponse(value, transportAuthorId),
            FcInventoryTransferRecord value => ValidateTransfer(value, transportAuthorId),
            FcEstateChestLocationRecord value => ValidateChestLocation(value, transportAuthorId),
            _ => FcValidationResult.Invalid($"Unsupported record type {record.GetType().FullName}.")
        };
    }

    public FcValidationResult Validate(
        FcMeshRecord envelope,
        object? payload,
        FcVerifiedMeshContext? verifiedContext = null,
        bool allowUnknownOptionalPublishedListMembers = false)
    {
        if (envelope is null || payload is null)
            return FcValidationResult.Invalid("Envelope or payload is null.");
        if (envelope.Payload is null || envelope.Payload.Length > MaxPayloadBytes
            || string.IsNullOrWhiteSpace(envelope.RecordId)
            || string.IsNullOrWhiteSpace(envelope.ActualAuthorId)
            || string.IsNullOrWhiteSpace(envelope.RecordType)
            || string.IsNullOrWhiteSpace(envelope.PayloadHash)
            || string.IsNullOrWhiteSpace(envelope.Signature)
            || string.IsNullOrWhiteSpace(envelope.SignatureAlgorithm)
            || string.IsNullOrWhiteSpace(envelope.DocumentKey))
            return FcValidationResult.Invalid("Envelope metadata or payload is incomplete.");
        if (envelope.ProtocolVersion != 0 && envelope.ProtocolVersion != FcProtocolVersion.Current)
            return FcValidationResult.Invalid("Envelope protocol version is incompatible.");
        if (!IsSafeIdentity(envelope.ActualAuthorId) || !IsSafeIdentity(envelope.Hlc.NodeId))
            return FcValidationResult.Invalid("Envelope author identity contains an invalid key-path character.");
        if (!Guid.TryParseExact(envelope.RecordId, "D", out var envelopeRecordId)
            || envelopeRecordId == Guid.Empty
            || !string.Equals(envelope.RecordId, envelopeRecordId.ToString("D"), StringComparison.Ordinal)
            || envelope.Revision == 0
            || envelope.Hlc.PhysicalUnixMs < 0
            || string.IsNullOrWhiteSpace(envelope.Hlc.NodeId))
            return FcValidationResult.Invalid("Envelope identity, revision, or HLC metadata is invalid.");
        if (!string.Equals(envelope.DocumentKeyOwnerId, envelope.ActualAuthorId, StringComparison.Ordinal)
            || !string.Equals(envelope.DocumentKey, FcMeshKey.ForRecord(envelope.RecordType, envelope.ActualAuthorId, envelope.RecordId), StringComparison.Ordinal))
            return FcValidationResult.Invalid("Verified document key does not match envelope author and record identity.");
        if (!string.Equals(envelope.PayloadHash, FcMeshSignature.HashPayload(envelope.Payload), StringComparison.Ordinal))
            return FcValidationResult.Invalid("Envelope payload hash is invalid.");
        if (!FcCanonical.SerializeUtf8(payload).SequenceEqual(envelope.Payload)
            && !(allowUnknownOptionalPublishedListMembers && payload is PublishedListRecord))
            return FcValidationResult.Invalid("Envelope payload does not match typed payload.");

        if (verifiedContext is null)
            return FcValidationResult.Invalid("Native signature verification context is required.");
        if (!_verifier.Verify(envelope, verifiedContext))
            return FcValidationResult.Invalid("Native signature verification context is invalid.");

        var header = GetHeader(payload);
        if (header is null
            || header.RecordId != envelopeRecordId
            || !string.Equals(header.OwnerAuthorId, envelope.ActualAuthorId, StringComparison.Ordinal)
            || header.Revision != envelope.Revision
            || !string.Equals(header.RecordType, envelope.RecordType, StringComparison.Ordinal))
            return FcValidationResult.Invalid("Inner header does not mirror native envelope metadata.");
        if (payload is WorkerSessionRecord worker)
        {
            if (envelope.Generation is null || envelope.Generation.Value != worker.SessionGeneration)
                return FcValidationResult.Invalid("Inner session generation does not mirror native envelope metadata.");
        }
        else if (payload is FcInventoryTransferRecord transfer)
        {
            if (envelope.Generation is null || envelope.Generation.Value != transfer.SessionGeneration)
                return FcValidationResult.Invalid("Transfer session generation does not mirror native envelope metadata.");
        }
        else if (envelope.Generation is not null)
        {
            return FcValidationResult.Invalid("This record type cannot carry a native generation.");
        }

        var payloadValidation = Validate(payload, envelope.ActualAuthorId);
        if (!payloadValidation.IsValid)
            return payloadValidation;
        if (payload is CapabilityRequestRecord request && request.ExpiresAt <= envelope.Hlc)
            return FcValidationResult.Invalid("Capability request expires at or before its authoritative HLC.");
        if (payload is CapabilityResponseRecord response && response.ExpiresAt <= envelope.Hlc)
            return FcValidationResult.Invalid("Capability response expires at or before its authoritative HLC.");
        return FcValidationResult.Valid;
    }

    private static FcRecordHeader? GetHeader(object record)
        => record switch
        {
            PublishedListRecord value => value.Header,
            WorkerSessionRecord value => value.Header,
            ChestSnapshotRecord value => value.Header,
            CapabilityRequestRecord value => value.Header,
            CapabilityResponseRecord value => value.Header,
            FcInventoryTransferRecord value => value.Header,
            FcEstateChestLocationRecord value => value.Header,
            _ => null,
        };

    private static FcValidationResult ValidateHeader(FcRecordHeader? header, string transportAuthorId, string expectedType)
    {
        if (header is null)
            return FcValidationResult.Invalid("Header is missing.");
        if (!header.IsCompatible)
            return FcValidationResult.Invalid("Protocol or schema version is incompatible.");
        if (!string.Equals(header.RecordType, expectedType, StringComparison.Ordinal))
            return FcValidationResult.Invalid("Record type does not match payload.");
        if (header.RecordId == Guid.Empty || string.IsNullOrWhiteSpace(header.OwnerAuthorId))
            return FcValidationResult.Invalid("Record ID and owner are required.");
        if (!IsSafeIdentity(header.OwnerAuthorId)
            || !IsSafeIdentity(transportAuthorId)
            || !string.Equals(header.OwnerAuthorId, transportAuthorId, StringComparison.Ordinal))
            return FcValidationResult.Invalid("Document-key author does not match record owner.");
        if (header.Revision == 0)
            return FcValidationResult.Invalid("Revision is invalid.");
        return FcValidationResult.Valid;
    }

    private static FcValidationResult ValidateList(PublishedListRecord record, string transportAuthorId)
    {
        var header = ValidateHeader(record.Header, transportAuthorId, FcRecordTypes.PublishedList);
        if (!header.IsValid)
            return header;
        if (record.ListId == Guid.Empty || string.IsNullOrWhiteSpace(record.DisplayName)
            || string.IsNullOrWhiteSpace(record.GameVersion)
            || record.PlannerSemanticsVersion < 0)
            return FcValidationResult.Invalid("Published list identity is invalid.");
        if (record.Header.RecordId != record.ListId)
            return FcValidationResult.Invalid("Published list record ID does not match list ID.");
        if (record.FinalTargets is null || record.FinalTargets.Length > MaxArrayEntries
            || record.FinalQualityPolicy is null || record.PrecraftQualityPolicy is null)
            return FcValidationResult.Invalid("Published list payload is incomplete.");
        var keys = new HashSet<(uint RecipeId, uint ItemId, FcItemQuality Quality)>();
        var totals = new Dictionary<(uint ItemId, FcItemQuality Quality), long>();
        foreach (var target in record.FinalTargets)
        {
            if (target is null || target.RecipeId == 0 || target.ItemId == 0 || target.Quantity <= 0
                || !Enum.IsDefined(target.Quality)
                || !keys.Add((target.RecipeId, target.ItemId, target.Quality)))
                return FcValidationResult.Invalid("Published list contains an invalid or duplicate target.");
            var total = totals.GetValueOrDefault((target.ItemId, target.Quality)) + target.Quantity;
            if (total > int.MaxValue)
                return FcValidationResult.Invalid("Published list target quantity overflows the protocol range.");
            totals[(target.ItemId, target.Quality)] = total;
        }
        var final = ValidateQualityPolicy(record.FinalQualityPolicy);
        return final.IsValid ? ValidateQualityPolicy(record.PrecraftQualityPolicy) : final;
    }

    private static FcValidationResult ValidateWorker(WorkerSessionRecord record, string transportAuthorId)
    {
        var header = ValidateHeader(record.Header, transportAuthorId, FcRecordTypes.WorkerSession);
        if (!header.IsValid)
            return header;
        if (record.SessionId == Guid.Empty || record.SessionGeneration == 0 || record.Character is null
            || string.IsNullOrWhiteSpace(record.Character.CharacterId)
            || string.IsNullOrWhiteSpace(record.Character.DisplayName)
            || string.IsNullOrWhiteSpace(record.Character.World)
            || record.Selection is null
            || record.HeldInventory is null || record.CraftQueue is null || record.GatherTargetOrder is null
            || string.IsNullOrWhiteSpace(record.WorldFingerprint))
            return FcValidationResult.Invalid("Worker session payload is incomplete.");
        if (!Enum.IsDefined(record.State))
            return FcValidationResult.Invalid("Worker state is invalid.");
        if (record.Selection.ListIds is null || record.Selection.ListIds.Length > MaxArrayEntries
            || record.Selection.ListIds.Any(listId => listId == Guid.Empty)
            || record.Selection.ListIds.Distinct().Count() != record.Selection.ListIds.Length
            || (record.Selection.AllPublishedLists && record.Selection.ListIds.Length != 0))
            return FcValidationResult.Invalid("Worker selection contains duplicate list IDs.");
        if (record.CraftQueue.Length > MaxArrayEntries || record.GatherTargetOrder.Length > MaxArrayEntries)
            return FcValidationResult.Invalid("Worker logical queues are too large.");
        var held = ValidateItems(record.HeldInventory);
        if (!held.IsValid)
            return held;
        foreach (var item in record.CraftQueue)
        {
            if (item is null || item.RecipeId == 0 || item.Remaining < 0
                || (item.ListId is { } listId && listId == Guid.Empty))
                return FcValidationResult.Invalid("Worker craft queue contains invalid quantity.");
        }
        if (record.CurrentTarget?.ItemId == 0 || record.CurrentTarget?.RecipeId == 0
            || record.CurrentTarget?.ListId == Guid.Empty
            || (record.CurrentTarget?.Quality is { } quality && !Enum.IsDefined(quality)))
            return FcValidationResult.Invalid("Worker logical target contains an empty identity.");
        return FcValidationResult.Valid;
    }

    private static FcValidationResult ValidateChest(ChestSnapshotRecord record, string transportAuthorId)
    {
        var header = ValidateHeader(record.Header, transportAuthorId, FcRecordTypes.ChestSnapshot);
        if (!header.IsValid)
            return header;
        if (record.Items is null || record.Crystals is null)
            return FcValidationResult.Invalid("Chest snapshot payload is incomplete.");
        if ((record.LoadedPageMask & ~CompleteChestPageMask) != 0)
            return FcValidationResult.Invalid("Chest snapshot contains an unknown page bit.");
        if (!record.Complete || record.LoadedPageMask != CompleteChestPageMask)
            return FcValidationResult.Invalid("Complete chest snapshot does not include every page and crystals.");
        var items = ValidateItems(record.Items);
        if (!items.IsValid)
            return items;
        return ValidateCrystals(record.Crystals);
    }

    private static FcValidationResult ValidateRequest(CapabilityRequestRecord record, string transportAuthorId)
    {
        var header = ValidateHeader(record.Header, transportAuthorId, FcRecordTypes.CapabilityRequest);
        if (!header.IsValid)
            return header;
        if (record.RequestId == Guid.Empty || string.IsNullOrWhiteSpace(record.WorldFingerprint)
            || record.ExpiresAt.PhysicalUnixMs < 0 || string.IsNullOrWhiteSpace(record.ExpiresAt.NodeId)
            || record.Recipes is null || record.Recipes.Length > MaxArrayEntries)
            return FcValidationResult.Invalid("Capability request payload is invalid.");
        if (record.Header.RecordId != record.RequestId)
            return FcValidationResult.Invalid("Capability request record ID does not match request ID.");
        if (!string.IsNullOrWhiteSpace(record.RequesterAuthorId)
            && !string.Equals(record.RequesterAuthorId, record.Header.OwnerAuthorId, StringComparison.Ordinal))
            return FcValidationResult.Invalid("Capability request requester does not match its author.");
        if (!IsSafeCapabilityText(record.GameVersion, 128)
            || !IsSafeCapabilityText(record.PlannerFingerprint, 256))
            return FcValidationResult.Invalid("Capability request compatibility metadata is invalid.");
        var recipes = new HashSet<uint>();
        foreach (var recipe in record.Recipes)
        {
            if (recipe is null || recipe.RecipeId == 0 || recipe.QualityPolicy is null
                || !recipes.Add(recipe.RecipeId))
                return FcValidationResult.Invalid("Capability request contains duplicate or invalid recipes.");
            var quality = ValidateQualityPolicy(recipe.QualityPolicy);
            if (!quality.IsValid)
                return quality;
            quality = ValidateQualityPolicy(recipe.FinalQualityPolicy);
            if (!quality.IsValid)
                return quality;
            quality = ValidateQualityPolicy(recipe.PrecraftQualityPolicy);
            if (!quality.IsValid)
                return quality;
            var effectivePolicy = recipe.IsPrecraft
                ? recipe.PrecraftQualityPolicy
                : recipe.FinalQualityPolicy;
            if (recipe.IsPrecraft && recipe.PrecraftQualityPolicy is null)
                return FcValidationResult.Invalid("Precraft capability policy is missing.");
            if (effectivePolicy is null
                || FcCanonical.Hash(recipe.QualityPolicy) != FcCanonical.Hash(effectivePolicy))
                return FcValidationResult.Invalid("Capability quality-policy alias is inconsistent with its effective policy.");
        }
        return FcValidationResult.Valid;
    }

    private static FcValidationResult ValidateResponse(CapabilityResponseRecord record, string transportAuthorId)
    {
        var header = ValidateHeader(record.Header, transportAuthorId, FcRecordTypes.CapabilityResponse);
        if (!header.IsValid)
            return header;
        if (record.RequestId == Guid.Empty || record.ExpiresAt.PhysicalUnixMs < 0
            || string.IsNullOrWhiteSpace(record.ExpiresAt.NodeId)
            || string.IsNullOrWhiteSpace(record.WorldFingerprint)
            || string.IsNullOrWhiteSpace(record.GameVersion)
            || string.IsNullOrWhiteSpace(record.PlannerFingerprint)
            || string.IsNullOrWhiteSpace(record.GearsetFingerprint)
            || string.IsNullOrWhiteSpace(record.SolverFingerprint)
            || record.Results is null || record.Results.Length > MaxArrayEntries)
            return FcValidationResult.Invalid("Capability response payload is invalid.");
        if (record.Header.RecordId != record.RequestId)
            return FcValidationResult.Invalid("Capability response record ID does not match request ID.");
        if (!string.IsNullOrWhiteSpace(record.ResponderAuthorId)
            && !string.Equals(record.ResponderAuthorId, record.Header.OwnerAuthorId, StringComparison.Ordinal))
            return FcValidationResult.Invalid("Capability response responder does not match its author.");
        if (record.SessionId != Guid.Empty && record.SessionGeneration == 0)
            return FcValidationResult.Invalid("Capability response session generation is invalid.");
        var recipes = new HashSet<uint>();
        foreach (var result in record.Results)
        {
            if (result is null || result.RecipeId == 0 || !Enum.IsDefined(result.Assessment)
                || (result.CanCraft && (result.SelectedJobId is null || result.SelectedJobId == 0))
                || (!result.CanCraft && result.SelectedJobId is not null)
                || (result.GuaranteesRequiredQuality
                    && (!result.CanCraft || result.Assessment != FcRaphaelAssessmentOutcome.FullQuality))
                || !recipes.Add(result.RecipeId))
                return FcValidationResult.Invalid("Capability response contains duplicate or invalid recipes.");
        }
        if (record.RequestedRecipes is not null)
        {
            if (record.RequestedRecipes.Length > MaxArrayEntries)
                return FcValidationResult.Invalid("Capability response requested recipe set is too large.");
            var requested = new HashSet<uint>();
            foreach (var recipe in record.RequestedRecipes)
            {
                if (recipe is null || recipe.RecipeId == 0 || !requested.Add(recipe.RecipeId))
                    return FcValidationResult.Invalid("Capability response requested recipe set is invalid.");
                var quality = ValidateQualityPolicy(recipe.QualityPolicy);
                if (!quality.IsValid)
                    return quality;
                quality = ValidateQualityPolicy(recipe.FinalQualityPolicy);
                if (!quality.IsValid)
                    return quality;
                quality = ValidateQualityPolicy(recipe.PrecraftQualityPolicy);
                if (!quality.IsValid)
                    return quality;
                var effectivePolicy = recipe.IsPrecraft
                    ? recipe.PrecraftQualityPolicy
                    : recipe.FinalQualityPolicy;
                if (effectivePolicy is null
                    || FcCanonical.Hash(recipe.QualityPolicy) != FcCanonical.Hash(effectivePolicy))
                    return FcValidationResult.Invalid("Capability response quality-policy alias is inconsistent with its effective policy.");
            }
            if (!requested.SetEquals(recipes))
                return FcValidationResult.Invalid("Capability response does not cover exactly its requested recipe set.");
        }
        return FcValidationResult.Valid;
    }

    private static FcValidationResult ValidateTransfer(FcInventoryTransferRecord record, string transportAuthorId)
    {
        var header = ValidateHeader(record.Header, transportAuthorId, FcRecordTypes.InventoryTransfer);
        if (!header.IsValid)
            return header;
        if (record.OperationId == Guid.Empty || record.SessionId == Guid.Empty || record.SessionGeneration == 0
            || record.ActualTransferred is null || record.ChestAfter is null || record.WorkerAfter is null
            || !Enum.IsDefined(record.Kind) || !Enum.IsDefined(record.Outcome))
            return FcValidationResult.Invalid("Inventory transfer payload is incomplete.");
        if (record.Header.RecordId != record.OperationId)
            return FcValidationResult.Invalid("Transfer record ID must equal operation ID.");
        var items = ValidateItems(record.ActualTransferred);
        if (!items.IsValid)
            return items;
        if (record.Outcome == FcInventoryTransferOutcome.Committed
            && record.ActualTransferred.All(entry => entry.Quantity == 0))
            return FcValidationResult.Invalid("Transfer must contain an observed physical quantity.");
        var chest = ValidateChest(record.ChestAfter, transportAuthorId);
        if (!chest.IsValid)
            return FcValidationResult.Invalid($"Nested chest state is invalid: {chest.Error}");
        var worker = ValidateWorker(record.WorkerAfter, transportAuthorId);
        if (!worker.IsValid)
            return FcValidationResult.Invalid($"Nested worker state is invalid: {worker.Error}");
        if (record.WorkerAfter.SessionId != record.SessionId
            || record.WorkerAfter.SessionGeneration != record.SessionGeneration)
            return FcValidationResult.Invalid("Nested worker session does not match transfer.");
        if (record.PurposeListId is { } purpose
            && !record.WorkerAfter.Selection.AllPublishedLists
            && !record.WorkerAfter.Selection.ListIds.Contains(purpose))
            return FcValidationResult.Invalid("Transfer purpose is outside the worker selection.");
        return FcValidationResult.Valid;
    }

    private static FcValidationResult ValidateChestLocation(
        FcEstateChestLocationRecord record,
        string transportAuthorId)
    {
        var header = ValidateHeader(record.Header, transportAuthorId, FcRecordTypes.FcChestLocation);
        if (!header.IsValid)
            return header;
        if (record.LocationId == Guid.Empty || record.Header.RecordId != record.LocationId
            || record.Housing is null || record.Environment is null || record.Chest is null
            || !IsSafeLocationText(record.CompatibilityFingerprint, 256))
            return FcValidationResult.Invalid("FC chest location identity or compatibility is incomplete.");
        if (!IsSafeLocationText(record.Housing.World, 128)
            || !IsSafeLocationText(record.Housing.Region, 128)
            || !IsSafeHousingDistrict(record.Housing.HousingDistrict)
            || record.Housing.Ward == 0 || record.Housing.Ward > 30
            || record.Housing.Plot == 0 || record.Housing.Plot > 60)
            return FcValidationResult.Invalid("FC housing address is invalid.");
        if (record.Environment.TerritoryId == 0 || record.Environment.MapId == 0
            || !IsSafeLocationText(record.Environment.TerritoryName, 128)
            || !float.IsFinite(record.Environment.ApproximateMapX)
            || !float.IsFinite(record.Environment.ApproximateMapY)
            || record.Environment.ApproximateMapX < 0
            || record.Environment.ApproximateMapY < 0
            || record.Environment.ApproximateMapX > 1000
            || record.Environment.ApproximateMapY > 1000)
            return FcValidationResult.Invalid("FC chest location environment anchor is invalid.");
        if (record.Chest.BaseId == 0
            || !IsSafeLocationText(record.Chest.ObjectKind, 64))
            return FcValidationResult.Invalid("FC chest stable object identity is invalid.");
        return FcValidationResult.Valid;
    }

    private static FcValidationResult ValidateItems(ItemQuantityEntry[]? entries)
    {
        if (entries is null || entries.Length > MaxArrayEntries)
            return FcValidationResult.Invalid("Item quantity array is missing or too large.");
        var keys = new HashSet<ItemQuantityKey>();
        foreach (var entry in entries)
        {
            if (entry is null || entry.ItemId == 0 || !Enum.IsDefined(entry.Quality) || entry.Quantity < 0)
                return FcValidationResult.Invalid("Item quantity contains a negative, overflowed, or invalid value.");
            if (!keys.Add(entry.Key))
                return FcValidationResult.Invalid("Item quantity array contains a duplicate key.");
        }
        return FcValidationResult.Valid;
    }

    private static FcValidationResult ValidateCrystals(CrystalQuantityMap crystals)
    {
        if (crystals.Entries is null || crystals.Entries.Length > MaxArrayEntries)
            return FcValidationResult.Invalid("Crystal quantity map is missing or too large.");
        var keys = new HashSet<uint>();
        foreach (var entry in crystals.Entries)
        {
            if (entry is null || entry.CrystalId == 0 || entry.Quantity < 0 || !keys.Add(entry.CrystalId))
                return FcValidationResult.Invalid("Crystal quantity contains an invalid or duplicate key.");
        }
        return FcValidationResult.Valid;
    }

    private static FcValidationResult ValidateQualityPolicy(FcQualityPolicy? policy)
    {
        if (policy is null || policy.Rules is null || policy.Rules.Length > MaxArrayEntries)
            return FcValidationResult.Invalid("Quality policy is missing or too large.");
        var keys = new HashSet<(uint, FcItemQuality)>();
        foreach (var rule in policy.Rules)
        {
            if (rule is null || rule.ItemId == 0 || rule.Quantity <= 0 || !Enum.IsDefined(rule.Quality)
                || !keys.Add((rule.ItemId, rule.Quality)))
                return FcValidationResult.Invalid("Quality policy contains an invalid or duplicate rule.");
        }
        return FcValidationResult.Valid;
    }

    private static bool IsSafeIdentity(string value)
        => !string.IsNullOrWhiteSpace(value)
            && !value.Contains('/')
            && !value.Contains('\\')
            && !value.Contains('\0')
            && !value.Contains('\r')
            && !value.Contains('\n');

    private static bool IsSafeLocationText(string? value, int maxLength)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= maxLength
            && !value.Contains('\0')
            && !value.Contains('\r')
            && !value.Contains('\n');

    private static bool IsSafeCapabilityText(string? value, int maxLength)
        => string.IsNullOrEmpty(value)
            || value.Length <= maxLength
            && !value.Contains('\0')
            && !value.Contains('\r')
            && !value.Contains('\n');

    private static bool IsSafeHousingDistrict(string? value)
        => value switch
        {
            "Lavender Beds" or "Mist" or "Goblet" or "Empyreum" or "Shirogane"
                => true,
            _ => false,
        };
}
