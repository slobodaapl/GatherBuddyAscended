using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GatherBuddy.FcMesh.Protocol;
using GatherBuddy.FcMesh.State;

namespace GatherBuddy.FcMesh.Publication;

/// <summary>
/// Atomic, per-character publication state. The file contains no native key,
/// ticket, or other secret; the character scope is only a selector for the
/// persisted register reservations.
/// </summary>
public sealed class FcFilePublicationStateStore : IFcPublicationStateStore
{
    private static readonly object ProcessGate = new();
    private static readonly byte[] InitializationMarkerBytes =
        Encoding.UTF8.GetBytes(FcPublicationState.CurrentInitializationMarker);
    private readonly string _directory;

    public FcFilePublicationStateStore(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("Publication state directory is required.", nameof(directory));
        _directory = directory;
    }

    public FcPublicationStateLoadResult Load(string authorScope)
    {
        lock (ProcessGate)
            return LoadCore(authorScope);
    }

    private FcPublicationStateLoadResult LoadCore(string authorScope)
    {
        if (string.IsNullOrWhiteSpace(authorScope))
            return new(
                FcPublicationStateLoadStatus.Corrupt,
                FcPublicationState.Create(authorScope ?? string.Empty),
                "Character author scope is unavailable.");
        var path = PathFor(authorScope);
        var markerPath = MarkerPathFor(authorScope);
        var stateExists = File.Exists(path);
        var markerExists = File.Exists(markerPath);
        if (!stateExists && !markerExists)
            return new(FcPublicationStateLoadStatus.Missing, FcPublicationState.Create(authorScope), string.Empty);
        if (!stateExists)
            return new(
                FcPublicationStateLoadStatus.Corrupt,
                FcPublicationState.Create(authorScope),
                "Publication initialization marker exists but state is missing.");
        if (!markerExists)
            return new(
                FcPublicationStateLoadStatus.Corrupt,
                FcPublicationState.Create(authorScope),
                "Publication state exists without its initialization marker.");

        try
        {
            ValidateInitializationMarker(markerPath);
            var state = JsonSerializer.Deserialize(
                File.ReadAllBytes(path),
                FcJsonContext.Default.FcPublicationState);
            if (state is null
                || state.Version != FcPublicationState.CurrentVersion
                || !string.Equals(state.BackendKind, FcPublicationState.CurrentBackendKind, StringComparison.Ordinal)
                || state.BackendStorageVersion != FcPublicationState.CurrentBackendStorageVersion
                || !string.Equals(state.AuthorScope, authorScope, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(state.BackendMarker)
                || state.ChestRecordId == Guid.Empty
                || state.Lists is null)
                throw new InvalidDataException("Publication state schema or scope is invalid.");
            var identities = new HashSet<string>(StringComparer.Ordinal);
            var publicIds = new HashSet<Guid>();
            foreach (var mapping in state.Lists)
            {
                if (mapping is null
                    || mapping.PublicListId == Guid.Empty
                    || mapping.CreatedAtUtc.Kind != DateTimeKind.Utc
                    || !identities.Add(new FcLocalListIdentity(mapping.LocalListId, mapping.CreatedAtUtc).StorageKey)
                    || !publicIds.Add(mapping.PublicListId))
                    throw new InvalidDataException("Publication list mapping identity is duplicated or invalid.");
            }
            ValidateRegisterState(state);
            return new(FcPublicationStateLoadStatus.Clean, state, string.Empty);
        }
        catch (Exception exception) when (exception is IOException
            or JsonException
            or InvalidDataException
            or ArgumentException
            or InvalidOperationException
            or NotSupportedException
            or NullReferenceException
            or OverflowException
            or UnauthorizedAccessException)
        {
            return new(
                FcPublicationStateLoadStatus.Corrupt,
                FcPublicationState.Create(authorScope),
                $"Publication state could not be loaded: {exception.Message}");
        }
    }

    private static void ValidateRegisterState(FcPublicationState state)
    {
        foreach (var mapping in state.Lists)
        {
            var published = mapping.LastPublishedSnapshot;
            var pendingSnapshot = mapping.PendingSnapshot;
            if (published is { Header: null } || pendingSnapshot is { Header: null })
                throw new InvalidDataException("Published-list snapshot header is missing.");
            var publishedRevision = published?.Header.Revision ?? 0;
            var pendingRevision = pendingSnapshot?.Header.Revision ?? 0;
            if (published is not null
                && (publishedRevision == 0
                    || published.ListId != mapping.PublicListId
                    || published.Header.RecordId != mapping.PublicListId
                    || !string.Equals(
                        mapping.LastPublishedHash,
                        FcCanonical.PayloadHash(published),
                        StringComparison.Ordinal)))
                throw new InvalidDataException("Published-list snapshot or hash is inconsistent.");
            if (published is null && !string.IsNullOrEmpty(mapping.LastPublishedHash))
                throw new InvalidDataException("Published-list hash has no snapshot.");
            if (mapping.ReservedRevision < publishedRevision || mapping.ReservedRevision < pendingRevision)
                throw new InvalidDataException("Published-list revision reservation regresses its snapshots.");
            if (pendingSnapshot is not null)
            {
                if (pendingSnapshot.Header is not { } pendingHeader)
                    throw new InvalidDataException("Published-list pending snapshot header is missing.");
                if (pendingSnapshot.ListId != mapping.PublicListId
                    || pendingHeader.RecordId != mapping.PublicListId)
                    throw new InvalidDataException("Published-list pending snapshot identity is inconsistent.");
            }
            if (published is not null
                && pendingSnapshot is not null
                && publishedRevision == pendingRevision
                && !string.Equals(
                    FcCanonical.PayloadHash(published),
                    FcCanonical.PayloadHash(pendingSnapshot),
                    StringComparison.Ordinal))
                throw new InvalidDataException("Published-list snapshots fork at one revision.");
            ValidatePendingCommand(
                mapping.Pending,
                pendingSnapshot,
                published,
                mapping.LastPublishedHash,
                "published-list");
        }

        var chest = state.LastChestSnapshot;
        var pendingChest = state.PendingChestSnapshot;
        if (chest is { Header: null } || pendingChest is { Header: null })
            throw new InvalidDataException("Chest snapshot header is missing.");
        var chestRevision = chest?.Header.Revision ?? 0;
        var pendingChestRevision = pendingChest?.Header.Revision ?? 0;
        if (chest is not null
            && (chestRevision == 0
                || chest.Header.RecordId != state.ChestRecordId
                || !chest.Complete
                || chest.LoadedPageMask != FcRecordValidator.CompleteChestPageMask
                || !string.Equals(
                    state.LastChestHash,
                    FcCanonical.PayloadHash(chest),
                    StringComparison.Ordinal)))
            throw new InvalidDataException("Chest snapshot or hash is inconsistent.");
        if (chest is null && !string.IsNullOrEmpty(state.LastChestHash))
            throw new InvalidDataException("Chest hash has no snapshot.");
        if (state.ChestReservedRevision < chestRevision
            || state.ChestReservedRevision < pendingChestRevision)
            throw new InvalidDataException("Chest revision reservation regresses its snapshots.");
        if (pendingChest is not null)
        {
            if (pendingChest.Header is not { } pendingHeader)
                throw new InvalidDataException("Chest pending snapshot header is missing.");
            if (pendingHeader.RecordId != state.ChestRecordId
                || !pendingChest.Complete
                || pendingChest.LoadedPageMask != FcRecordValidator.CompleteChestPageMask)
                throw new InvalidDataException("Chest pending snapshot identity or completeness is inconsistent.");
        }
        if (chest is not null
            && pendingChest is not null
            && chestRevision == pendingChestRevision
            && !string.Equals(
                FcCanonical.PayloadHash(chest),
                FcCanonical.PayloadHash(pendingChest),
                StringComparison.Ordinal))
            throw new InvalidDataException("Chest snapshots fork at one revision.");
        ValidatePendingCommand(
            state.PendingChest,
            pendingChest,
            chest,
            state.LastChestHash,
            "chest");
    }

    private static void ValidatePendingCommand(
        FcPublicationCommandState? command,
        PublishedListRecord? pendingSnapshot,
        PublishedListRecord? acceptedSnapshot,
        string acceptedHash,
        string registerName)
    {
        if (command is null)
        {
            if (pendingSnapshot is not null)
                throw new InvalidDataException($"{registerName} pending snapshot has no command.");
            return;
        }
        if (!Enum.IsDefined(command.Kind)
            || command.Status == FcPublicationCommandStatus.None
            || command.Revision == 0
            || string.IsNullOrWhiteSpace(command.PayloadHash))
            throw new InvalidDataException($"{registerName} pending command is invalid.");
        if (command.Kind is not (FcPublicationCommandKind.Publish
            or FcPublicationCommandKind.Update
            or FcPublicationCommandKind.Unpublish))
            throw new InvalidDataException($"{registerName} pending command type is invalid.");
        if (pendingSnapshot is { } pending)
        {
            if (pending.Header is null)
                throw new InvalidDataException($"{registerName} pending snapshot header is missing.");
            if (pending.Header.Revision != command.Revision
                || !string.Equals(
                    command.PayloadHash,
                    FcCanonical.PayloadHash(pending),
                    StringComparison.Ordinal))
                throw new InvalidDataException($"{registerName} pending command does not match its snapshot.");
            if (pending.Published != (command.Kind != FcPublicationCommandKind.Unpublish))
                throw new InvalidDataException($"{registerName} pending command publication state is inconsistent.");
            return;
        }
        if (command.Status != FcPublicationCommandStatus.AcceptedByNative
            || acceptedSnapshot is null
            || acceptedSnapshot.Header is null
            || acceptedSnapshot.Header.Revision != command.Revision
            || !string.Equals(command.PayloadHash, acceptedHash, StringComparison.Ordinal))
            throw new InvalidDataException($"{registerName} accepted command has no matching snapshot.");
    }

    private static void ValidatePendingCommand(
        FcPublicationCommandState? command,
        ChestSnapshotRecord? pendingSnapshot,
        ChestSnapshotRecord? acceptedSnapshot,
        string acceptedHash,
        string registerName)
    {
        if (command is null)
        {
            if (pendingSnapshot is not null)
                throw new InvalidDataException($"{registerName} pending snapshot has no command.");
            return;
        }
        if (!Enum.IsDefined(command.Kind)
            || command.Status == FcPublicationCommandStatus.None
            || command.Revision == 0
            || (command.Status != FcPublicationCommandStatus.AwaitingFreshObservation
                && command.Status != FcPublicationCommandStatus.Failed
                && string.IsNullOrWhiteSpace(command.PayloadHash)))
            throw new InvalidDataException($"{registerName} pending command is invalid.");
        if (command.Kind != FcPublicationCommandKind.ChestObservation)
            throw new InvalidDataException($"{registerName} pending command type is invalid.");
        if (command.Status == FcPublicationCommandStatus.AwaitingFreshObservation)
        {
            if (pendingSnapshot is not null)
                throw new InvalidDataException($"{registerName} fresh observation reservation already has a snapshot.");
            return;
        }
        if (pendingSnapshot is { } pending)
        {
            if (pending.Header is null)
                throw new InvalidDataException($"{registerName} pending snapshot header is missing.");
            if (pending.Header.Revision != command.Revision
                || !string.Equals(
                    command.PayloadHash,
                    FcCanonical.PayloadHash(pending),
                    StringComparison.Ordinal))
                throw new InvalidDataException($"{registerName} pending command does not match its snapshot.");
            return;
        }
        if (command.Status == FcPublicationCommandStatus.Failed)
            return;
        if (command.Status != FcPublicationCommandStatus.AcceptedByNative
            || acceptedSnapshot is null
            || acceptedSnapshot.Header is null
            || acceptedSnapshot.Header.Revision != command.Revision
            || !string.Equals(command.PayloadHash, acceptedHash, StringComparison.Ordinal))
            throw new InvalidDataException($"{registerName} accepted command has no matching snapshot.");
    }

    public void Save(string authorScope, FcPublicationState state)
    {
        lock (ProcessGate)
            SaveCore(authorScope, state);
    }

    private void SaveCore(string authorScope, FcPublicationState state)
    {
        if (string.IsNullOrWhiteSpace(authorScope))
            throw new ArgumentException("Character author scope is required.", nameof(authorScope));
        if (state is null)
            throw new ArgumentNullException(nameof(state));
        if (!string.Equals(state.AuthorScope, authorScope, StringComparison.Ordinal))
            throw new InvalidDataException("Publication state scope does not match its file scope.");
        if (state.Version != FcPublicationState.CurrentVersion
            || !string.Equals(state.BackendKind, FcPublicationState.CurrentBackendKind, StringComparison.Ordinal)
            || state.BackendStorageVersion != FcPublicationState.CurrentBackendStorageVersion)
            throw new InvalidDataException("Publication state backend schema is unsupported.");

        Directory.CreateDirectory(_directory);
        var path = PathFor(authorScope);
        var markerPath = MarkerPathFor(authorScope);
        var stateExists = File.Exists(path);
        var markerExists = File.Exists(markerPath);
        if (stateExists || markerExists)
        {
            var current = LoadCore(authorScope);
            if (current.Status != FcPublicationStateLoadStatus.Clean)
            {
                if (current.Status != FcPublicationStateLoadStatus.Missing)
                    throw new InvalidDataException(
                        string.IsNullOrWhiteSpace(current.Error)
                            ? "Existing publication state is not writable."
                            : current.Error);
            }
            else
                state = FcPublicationStateMerge.Merge(current.State, state);
        }
        EnsureInitializationMarker(markerPath, stateExists);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(state, FcJsonContext.Default.FcPublicationState);
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }

            // File.Move with overwrite is the portable atomic replace used by
            // the plugin's narrow state file. The old file is never truncated.
            File.Move(temporary, path, true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            catch
            {
                // Preserve the original persistence failure. The next load
                // remains fail-closed if the replacement did not complete.
            }
        }
    }

    private string PathFor(string authorScope)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(authorScope));
        var file = "publication-" + Convert.ToHexString(digest).ToLowerInvariant() + ".json";
        return Path.Combine(_directory, file);
    }

    private string MarkerPathFor(string authorScope)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(authorScope));
        var file = "publication-" + Convert.ToHexString(digest).ToLowerInvariant() + ".marker";
        return Path.Combine(_directory, file);
    }

    private static void ValidateInitializationMarker(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (!bytes.AsSpan().SequenceEqual(InitializationMarkerBytes))
            throw new InvalidDataException("Publication initialization marker is invalid.");
    }

    private static void EnsureInitializationMarker(string path, bool stateExists)
    {
        // The marker detects partial local initialization/rollback. An
        // arbitrary simultaneous rollback of every local and native copy is
        // outside what this local store can detect.
        if (File.Exists(path))
        {
            ValidateInitializationMarker(path);
            return;
        }
        if (stateExists)
            throw new InvalidDataException("Publication state exists without its initialization marker.");

        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       256,
                       FileOptions.WriteThrough))
            {
                stream.Write(InitializationMarkerBytes);
                stream.Flush(true);
            }
            File.Move(temporary, path, false);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            catch
            {
                // Preserve the marker commit failure. A missing marker keeps
                // the next load distinguishable from a committed state.
            }
        }
    }
}
