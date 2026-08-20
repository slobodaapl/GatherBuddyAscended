using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace GatherBuddy.FcMesh.Publication;

/// <summary>
/// Reads the game compatibility version selected by the loaded Dalamud hook.
/// The hook's version.json is host-owned metadata, not this plugin's version.
/// </summary>
public sealed class FcGameVersionProvider
{
    private readonly Task<string?> _load;

    public FcGameVersionProvider(string hostAssemblyPath)
    {
        _load = Task.Run(() => Load(hostAssemblyPath));
    }

    public string? CurrentVersion
        => _load.IsCompletedSuccessfully ? _load.Result : null;

    public string Status
        => !_load.IsCompleted ? "Reading Dalamud game compatibility metadata..."
            : CurrentVersion is { } ? $"Dalamud SupportedGameVer: {CurrentVersion}"
            : "Dalamud game compatibility metadata unavailable.";

    /// <summary>
    /// Parses the exact host metadata field without accepting plugin or assembly
    /// version fields as a substitute.
    /// </summary>
    public static string? ParseSupportedGameVersion(ReadOnlySpan<byte> json)
    {
        try
        {
            using var document = JsonDocument.Parse(json.ToArray().AsMemory());
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("SupportedGameVer", out var value)
                || value.ValueKind != JsonValueKind.String)
                return null;
            var version = value.GetString()?.Trim();
            return version is not null && IsSupportedGameVersion(version) ? version : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsSupportedGameVersion(string version)
    {
        var components = version.Split('.');
        return components.Length == 5
            && IsDigits(components[0], 4)
            && IsDigits(components[1], 2)
            && IsDigits(components[2], 2)
            && IsDigits(components[3], 4)
            && IsDigits(components[4], 4);
    }

    private static bool IsDigits(string component, int length)
    {
        if (component.Length != length)
            return false;
        foreach (var character in component)
        {
            if (character < '0' || character > '9')
                return false;
        }
        return true;
    }

    private static string? Load(string hostAssemblyPath)
    {
        try
        {
            var hostDirectory = Path.GetDirectoryName(hostAssemblyPath);
            if (string.IsNullOrWhiteSpace(hostDirectory))
                return null;
            var metadataPath = Path.Combine(hostDirectory, "version.json");
            return ParseSupportedGameVersion(File.ReadAllBytes(metadataPath));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
