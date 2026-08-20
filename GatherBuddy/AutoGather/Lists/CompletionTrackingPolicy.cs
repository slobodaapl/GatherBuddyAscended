using GatherBuddy.FcMesh.Protocol;

namespace GatherBuddy.AutoGather.Lists;

internal readonly record struct CompletionTrackingDescriptor(
    uint ItemId,
    uint CompletionItemId,
    FcItemQuality? Quality,
    string Scope,
    string ProviderKey);

internal static class CompletionTrackingPolicy
{
    internal const string PhysicalProviderKey = "physical";

    internal static CompletionTrackingDescriptor Create(
        uint itemId,
        uint completionItemId,
        FcItemQuality? quality,
        string? scope,
        string? providerKey = null)
        => new(
            itemId,
            completionItemId,
            quality,
            string.IsNullOrWhiteSpace(scope) ? AutoGatherList.DefaultCompletionScope : scope,
            string.IsNullOrWhiteSpace(providerKey) ? PhysicalProviderKey : providerKey);

    internal static string GetProviderKey(ICompletionCountProvider? provider)
        => provider switch
        {
            null or DefaultCompletionCountProvider => PhysicalProviderKey,
            FcCompletionCountProvider => "represented",
            _ => provider.GetType().FullName ?? provider.GetType().Name,
        };

    internal static bool CanAggregate(
        CompletionTrackingDescriptor left,
        CompletionTrackingDescriptor right)
        => left == right;

    internal static bool Matches(
        CompletionTrackingDescriptor expected,
        CompletionTrackingDescriptor observed)
        => CanAggregate(expected, observed);

    internal static bool IsCompleteFor(
        CompletionTrackingDescriptor expected,
        CompletionTrackingDescriptor observed,
        int completionCount,
        uint requiredQuantity)
        => Matches(expected, observed)
        && completionCount >= 0
        && (ulong)completionCount >= requiredQuantity;
}
