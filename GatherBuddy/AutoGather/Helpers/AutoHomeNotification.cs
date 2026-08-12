namespace GatherBuddy.AutoGather.Helpers;

internal static class AutoHomeNotification
{
    internal static string Build(string reason)
        => $"[GatherBuddy Ascended] Auto-home navigation triggered: {reason}. To disable this behavior, open Settings > Auto-Gather and disable 'Go home when done' and/or 'Go home when idle'. Disable 'Show auto-home chat warning' there to silence this message.";
}
