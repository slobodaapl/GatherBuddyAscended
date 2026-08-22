using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using GatherBuddy.FcMesh.Protocol;

namespace GatherBuddy.FcMesh.Publication;

/// <summary>
/// Forward-compatible decoder for the published-list payload only. The
/// canonical native envelope remains strict; this context applies the
/// public-list schema rule that unknown optional payload members are ignored.
/// A planner/game compatibility mismatch is still retained in the record and
/// is rejected by <see cref="FcPublishedListCompatibility"/> for execution.
/// </summary>
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Default,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = false,
    WriteIndented = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip)]
[JsonSerializable(typeof(PublishedListRecord))]
internal partial class FcPublishedListPayloadCompatibilityJsonContext : JsonSerializerContext
{
}

internal static class FcPublishedListPayloadCompatibility
{
    public static PublishedListRecord Deserialize(ReadOnlySpan<byte> payload)
        => JsonSerializer.Deserialize(
               payload,
               FcPublishedListPayloadCompatibilityJsonContext.Default.PublishedListRecord)
           ?? throw new JsonException("Published-list payload is null.");
}
