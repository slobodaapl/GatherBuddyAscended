using System;
using System.IO;
using Dalamud.Plugin;
using ElliLib.Log;

namespace GatherBuddy.Config;

internal sealed class GatherBuddyRebornMigration
{
    private const string LegacyInternalName = "GatherBuddyReborn";
    private const string Pending = "pending";
    private const string Migrated = "migrated";
    private const string Declined = "declined";
    private const string NoSource = "no-source";

    private readonly DirectoryInfo _sourceDirectory;
    private readonly DirectoryInfo _targetDirectory;
    private readonly FileInfo _sourceConfigFile;
    private readonly FileInfo _targetConfigFile;
    private readonly string _stateFile;
    private readonly Logger _log;

    private GatherBuddyRebornMigration(IDalamudPluginInterface pluginInterface, Logger log)
    {
        _targetDirectory = pluginInterface.ConfigDirectory;
        _targetConfigFile = pluginInterface.ConfigFile;
        var configRoot = _targetDirectory.Parent
            ?? throw new InvalidOperationException("Could not resolve the Dalamud plugin configuration root.");
        _sourceDirectory = new DirectoryInfo(Path.Combine(configRoot.FullName, LegacyInternalName));
        _sourceConfigFile = new FileInfo(Path.Combine(_targetConfigFile.DirectoryName!, $"{LegacyInternalName}.json"));
        _stateFile = $"{_targetConfigFile.FullName}.reborn-migration";
        _log = log;
    }

    internal static GatherBuddyRebornMigration Prepare(IDalamudPluginInterface pluginInterface, Logger log)
    {
        var migration = new GatherBuddyRebornMigration(pluginInterface, log);
        migration.ApplyPendingMigration();
        return migration;
    }

    internal bool ShouldPrompt
    {
        get
        {
            if (File.Exists(_stateFile))
                return false;

            if (_sourceDirectory.Exists)
                return true;

            WriteState(NoSource);
            return false;
        }
    }

    internal string SourceDirectory => _sourceDirectory.FullName;

    internal void ScheduleMigration()
        => WriteState(Pending);

    internal void DeclineMigration()
        => WriteState(Declined);

    private void ApplyPendingMigration()
    {
        if (!File.Exists(_stateFile) || File.ReadAllText(_stateFile).Trim() != Pending)
            return;

        if (!_sourceDirectory.Exists)
        {
            _log.Warning($"GatherBuddy Reborn migration was pending, but the source directory no longer exists: {_sourceDirectory.FullName}");
            File.Delete(_stateFile);
            return;
        }

        CopyDirectory(_sourceDirectory.FullName, _targetDirectory.FullName);
        if (_sourceConfigFile.Exists)
        {
            Directory.CreateDirectory(_targetConfigFile.DirectoryName!);
            _sourceConfigFile.CopyTo(_targetConfigFile.FullName, true);
        }

        WriteState(Migrated);
        _log.Information($"Migrated GatherBuddy Reborn configuration and state from {_sourceDirectory.FullName}.");
    }

    private void WriteState(string state)
    {
        Directory.CreateDirectory(_targetConfigFile.DirectoryName!);
        File.WriteAllText(_stateFile, state);
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(target, relativePath));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, true);
        }
    }
}
