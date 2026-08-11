using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using GatherBuddy.Config;

namespace GatherBuddy.Gui;

internal sealed class GatherBuddyRebornMigrationWindow : Window
{
    private readonly GatherBuddyRebornMigration _migration;
    private bool _migrationScheduled;

    internal GatherBuddyRebornMigrationWindow(GatherBuddyRebornMigration migration)
        : base("Welcome to GatherBuddy Ascended###GatherBuddyAscendedMigration")
    {
        _migration = migration;
        IsOpen = true;
        ShowCloseButton = true;
        RespectCloseHotkey = true;
        Size = new Vector2(580f, 0f);
        SizeCondition = ImGuiCond.Appearing;
        Flags = ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoDocking;
    }

    public override void Draw()
    {
        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.35f, 0.80f, 1.00f, 1.00f));
        ImGui.SetWindowFontScale(1.25f);
        ImGui.TextUnformatted("Welcome to GatherBuddy Ascended!");
        ImGui.SetWindowFontScale(1.00f);
        ImGui.PopStyleColor();

        ImGui.Spacing();
        ImGui.TextWrapped("We found an existing GatherBuddy Reborn configuration folder. GatherBuddy Ascended can bring your settings, lists, alarms, recorded state, and other plugin data forward.");
        ImGui.Spacing();
        ImGui.TextDisabled($"Found: {_migration.SourceDirectory}");
        ImGui.Spacing();

        if (_migrationScheduled)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.40f, 0.90f, 0.55f, 1.00f));
            ImGui.TextWrapped("Migration scheduled successfully.");
            ImGui.PopStyleColor();
            ImGui.TextWrapped("Reload GatherBuddy Ascended or restart the game. Migration will run before the plugin loads next time.");
            ImGui.Separator();
            if (ImGui.Button("Close", new Vector2(-1f, 0f)))
                IsOpen = false;
            return;
        }

        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1.00f, 0.72f, 0.25f, 1.00f));
        ImGui.TextWrapped("Warning: migrating replaces every conflicting GatherBuddy Ascended configuration and state file with the GatherBuddy Reborn version.");
        ImGui.PopStyleColor();
        ImGui.Separator();

        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var width = (ImGui.GetContentRegionAvail().X - spacing * 2f) / 3f;
        if (ImGui.Button("Migrate from Reborn", new Vector2(width, 0f)))
        {
            _migration.ScheduleMigration();
            _migrationScheduled = true;
        }

        ImGui.SameLine();
        if (ImGui.Button("Decide later", new Vector2(width, 0f)))
            IsOpen = false;

        ImGui.SameLine();
        if (ImGui.Button("Don't migrate", new Vector2(width, 0f)))
        {
            _migration.DeclineMigration();
            IsOpen = false;
        }

        ImGui.Spacing();
        ImGui.TextDisabled("Closing this window or choosing Decide later will ask again next time.");
    }
}
