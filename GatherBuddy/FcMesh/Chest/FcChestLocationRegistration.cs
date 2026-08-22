using System;
using GatherBuddy.FcMesh.Protocol;
using GatherBuddy.FcMesh.State;

namespace GatherBuddy.FcMesh.Chest;

public sealed record FcChestLocationObservation(
    FcHousingAddress Housing,
    FcChestLocationEnvironment Environment,
    FcChestObjectIdentity Chest,
    string CompatibilityFingerprint);

public static class FcChestLocationRegister
{
    public static readonly Guid LocationId = Guid.Parse("3bda7fd9-01f6-4d9f-a3b8-3b9f8c3aa7b2");

    public static FcEstateChestLocationRecord Create(
        FcChestLocationObservation observation,
        string author,
        ulong revision,
        bool published = true)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (string.IsNullOrWhiteSpace(author))
            throw new ArgumentException("Location author is required.", nameof(author));
        if (revision == 0)
            throw new ArgumentOutOfRangeException(nameof(revision));
        var record = new FcEstateChestLocationRecord(
            new FcRecordHeader(
                FcProtocolVersion.Current,
                FcProtocolVersion.CurrentSchema,
                FcRecordTypes.FcChestLocation,
                LocationId,
                author,
                revision),
            LocationId,
            published,
            observation.Housing,
            observation.Environment,
            observation.Chest,
            observation.CompatibilityFingerprint);
        var validation = new FcRecordValidator().Validate(record, author);
        if (!validation.IsValid)
            throw new ArgumentException(validation.Error, nameof(observation));
        return record;
    }

    public static FcEstateChestLocationRecord CreateTombstone(
        FcEstateChestLocationRecord previous,
        string author,
        ulong revision)
    {
        ArgumentNullException.ThrowIfNull(previous);
        if (string.IsNullOrWhiteSpace(author))
            throw new ArgumentException("Location author is required.", nameof(author));
        if (revision == 0)
            throw new ArgumentOutOfRangeException(nameof(revision));
        var record = previous with
        {
            Header = previous.Header with
            {
                OwnerAuthorId = author,
                Revision = revision,
                RecordType = FcRecordTypes.FcChestLocation,
            },
            Published = false,
        };
        var validation = new FcRecordValidator().Validate(record, author);
        if (!validation.IsValid)
            throw new ArgumentException(validation.Error, nameof(previous));
        return record;
    }
}
