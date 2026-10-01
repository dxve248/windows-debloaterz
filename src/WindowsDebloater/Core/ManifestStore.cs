using System.IO;
using System.Text.Json;

namespace WindowsDebloater.Core;

/// <summary>
/// Everything a run needs to be undone lives in one JSON file. It is written as
/// each change is applied, not at the end, so a run that is interrupted still
/// leaves a usable undo trail.
/// </summary>
public sealed class ManifestStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string Root => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WindowsDebloater");

    public static string LatestPath => Path.Combine(Root, "manifest-latest.json");

    public static void EnsureRoot() => Directory.CreateDirectory(Root);

    public static string Stamp() => DateTime.Now.ToString("yyyyMMdd-HHmmss");

    public static RunManifest CreateNew()
    {
        EnsureRoot();
        return new RunManifest
        {
            Timestamp = Stamp(),
            Machine = Environment.MachineName,
            User = Environment.UserName
        };
    }

    /// <summary>
    /// Writes the manifest to a timestamped history file and to "latest".
    /// Both go through a temp file and a rename: the manifest is rewritten after
    /// every single change, so a crash mid-write would otherwise leave a
    /// truncated file with no undo trail.
    /// </summary>
    public static string Save(RunManifest manifest)
    {
        EnsureRoot();

        var historyPath = Path.Combine(Root, $"manifest-{manifest.Timestamp}.json");
        var json = JsonSerializer.Serialize(manifest, Options);

        WriteAtomic(historyPath, json);
        WriteAtomic(LatestPath, json);

        return historyPath;
    }

    private static void WriteAtomic(string path, string content)
    {
        var temp = path + ".tmp";

        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }

    public static RunManifest? LoadLatest() => File.Exists(LatestPath) ? Load(LatestPath) : null;

    /// <summary>
    /// Moves manifest-latest.json aside once its changes have been reverted, so a
    /// second click on Undo does not try to undo the same run twice.
    /// </summary>
    public static void MarkUndone()
    {
        if (!File.Exists(LatestPath)) return;

        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Move(LatestPath, Path.Combine(Root, $"manifest-{stamp}-undone.json"), overwrite: true);
        }
        catch (IOException)
        {
            // Losing the rename only costs us a duplicate undo attempt, which is
            // harmless because undo is idempotent.
        }
    }

    public static RunManifest? Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<RunManifest>(File.ReadAllText(path), Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string NewLogPath()
    {
        EnsureRoot();
        return Path.Combine(Root, $"changes-{Stamp()}.log");
    }

    public static void AppendLog(string path, string line) =>
        File.AppendAllText(path, line + Environment.NewLine);
}