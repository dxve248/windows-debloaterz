using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowsDebloater.Core;

internal sealed class AppsFile
{
    public int Version { get; set; }
    public List<AppEntry> Apps { get; set; } = [];
}

internal sealed class TweaksFile
{
    public int Version { get; set; }
    public List<Tweak> Tweaks { get; set; } = [];
}

/// <summary>
/// Loads the two catalogs. An on-disk copy next to the executable wins over the
/// embedded copy, so the selection can be edited without rebuilding.
/// </summary>
public sealed class Catalog
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public List<AppEntry> Apps { get; }
    public List<Tweak> Tweaks { get; }

    /// <summary>Where the catalogs were loaded from, shown in the UI footer.</summary>
    public string Source { get; }

    private Catalog(List<AppEntry> apps, List<Tweak> tweaks, string source)
    {
        Apps = apps;
        Tweaks = tweaks;
        Source = source;
    }

    public static Catalog Load()
    {
        var disk = Path.Combine(AppContext.BaseDirectory, "Data");
        var appOnDisk = Path.Combine(disk, "Apps.json");
        var tweakOnDisk = Path.Combine(disk, "Tweaks.json");

        if (File.Exists(appOnDisk) && File.Exists(tweakOnDisk))
        {
            return new Catalog(
                ReadApps(appOnDisk),
                ReadTweaks(tweakOnDisk),
                "on-disk catalogs");
        }

        return new Catalog(
            ReadEmbedded<AppEntry>("Apps.json", "apps"),
            ReadEmbedded<Tweak>("Tweaks.json", "tweaks"),
            "built-in catalogs");
    }

    private static List<AppEntry> ReadApps(string path) =>
        JsonSerializer.Deserialize<AppsFile>(File.ReadAllText(path), Options)?.Apps
        ?? throw new InvalidDataException($"No 'apps' array found in {path}.");

    private static List<Tweak> ReadTweaks(string path) =>
        JsonSerializer.Deserialize<TweaksFile>(File.ReadAllText(path), Options)?.Tweaks
        ?? throw new InvalidDataException($"No 'tweaks' array found in {path}.");

    private static List<T> ReadEmbedded<T>(string fileName, string arrayProperty)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = Array.Find(
            assembly.GetManifestResourceNames(),
            n => n.EndsWith("." + fileName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Embedded resource '{fileName}' was not found.");

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty(arrayProperty, out var array))
        {
            throw new InvalidDataException($"No '{arrayProperty}' array found in embedded {fileName}.");
        }

        return [.. array.EnumerateArray().Select(e => e.Deserialize<T>(Options)!)];
    }
}