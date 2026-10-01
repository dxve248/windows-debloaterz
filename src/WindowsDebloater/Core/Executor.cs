using System.IO;
using System.Text;

namespace WindowsDebloater.Core;

public sealed record ApplyProgress(
    ActionItem? Item,
    int Index,
    int Total,
    string Message,
    bool Failed)
{
    public double Fraction => Total == 0 ? 0 : (double)Index / Total;
}

public sealed record UndoStep(int Index, int Total, string Title, string Message, bool Failed)
{
    public double Fraction => Total == 0 ? 0 : (double)Index / Total;
}

public sealed class RunResult
{
    public int Total { get; set; }
    public int Succeeded { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public List<string> Errors { get; } = [];
    public List<UndoRecord> Unrestorable { get; } = [];
    public RunManifest Manifest { get; set; } = new();
    public string ManifestPath { get; set; } = "";
    public string LogPath { get; set; } = "";
    public string RestorePointWarning { get; set; } = "";
    public bool RestorePointCreated => Manifest.RestorePointCreated;

    /// <summary>
    /// True when this run changed something that only takes full effect after a
    /// restart. Deliberately not derived from the record count: most registry and
    /// Appx changes apply immediately, so claiming a reboot is needed whenever
    /// anything at all changed would be wrong.
    /// </summary>
    public bool RequiresReboot =>
        Manifest.Records.Any(r => r.RequiresReboot)
        || RebootPendingKeys.Any(k => RegistryOps.KeyExists(k.Scope, k.Path))
        || RebootPendingValues.Any(v => RegistryOps.Read(v.Scope, v.Path, v.ValueName).Exists)
        || ResultWasInterrupted;

    /// <summary>Set when the run threw before finishing, so state may be half-applied.</summary>
    public bool ResultWasInterrupted { get; set; }

    /// <summary>Keys Windows creates when a restart is already outstanding.</summary>
    private static readonly (RegistryScope Scope, string Path)[] RebootPendingKeys =
    [
        (RegistryScope.LocalMachine,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"),
        (RegistryScope.LocalMachine,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired")
    ];

    /// <summary>
    /// "Session Manager" always exists, so these pending file replacement lists
    /// have to be checked as values on that key rather than as keys.
    /// </summary>
    private static readonly (RegistryScope Scope, string Path, string ValueName)[] RebootPendingValues =
    [
        (RegistryScope.LocalMachine,
            @"SYSTEM\CurrentControlSet\Control\Session Manager", "PendingFileRenameOperations"),
        (RegistryScope.LocalMachine,
            @"SYSTEM\CurrentControlSet\Control\Session Manager", "PendingFileRenameOperations2")
    ];
}

public sealed class UndoResult
{
    public int Total { get; set; }
    public int Restored { get; set; }
    public int Failed { get; set; }
    public List<string> Errors { get; } = [];
    public List<string> Manual { get; } = [];
}

/// <summary>
/// Applies a plan and records how to reverse it. Each change is captured
/// immediately before it is written, so the manifest describes reality rather
/// than what we assumed when the plan was built.
/// </summary>
public static class Executor
{
    public static RunResult Apply(
        IReadOnlyList<ActionItem> items,
        bool createRestorePoint,
        IProgress<ApplyProgress>? progress = null,
        CancellationToken cancellation = default)
    {
        var result = new RunResult { Total = items.Count };
        var manifest = ManifestStore.CreateNew();
        var logPath = ManifestStore.NewLogPath();

        result.Manifest = manifest;
        result.LogPath = logPath;

        var header = new StringBuilder()
            .AppendLine($"Windows Debloater run {manifest.Timestamp}")
            .AppendLine($"Machine: {manifest.Machine}    User: {manifest.User}")
            .AppendLine(new string('-', 78))
            .ToString();

        try { File.WriteAllText(logPath, header); } catch { /* logging is best effort */ }

        void Log(string line)
        {
            try { File.AppendAllText(logPath, line + Environment.NewLine); } catch { }
        }

        if (createRestorePoint)
        {
            var description = "Before_Debloat_" + manifest.Timestamp;
            progress?.Report(new ApplyProgress(null, 0, items.Count, "Creating a system restore point...", false));

            try
            {
                PowerShell.CreateRestorePoint(description);
                manifest.RestorePointCreated = true;
                manifest.RestorePointDescription = description;
                Log($"Restore point created: {description}");
            }
            catch (Exception ex)
            {
                result.RestorePointWarning =
                    "A system restore point could not be created, so there is no machine-wide " +
                    "safety net for this run. Every change is still individually recorded and can " +
                    "be reverted with Undo Last Run.";
                Log("Restore point FAILED: " + ex.Message);
            }
        }

        var index = 0;
        foreach (var item in items)
        {
            index++;

            if (cancellation.IsCancellationRequested)
            {
                // Flagged before throwing so the caller can warn that some
                // changes are applied and the machine is mid-transition.
                result.ResultWasInterrupted = true;
                cancellation.ThrowIfCancellationRequested();
            }

            if (item.AlreadyDone || item.NotApplicable)
            {
                result.Skipped++;
                Log($"SKIP  [{item.Kind}] {item.Title} - {item.Current}");
                progress?.Report(new ApplyProgress(item, index, items.Count, "skipped - " + item.Current, false));
                continue;
            }

            try
            {
                var record = ApplyOne(item);
                manifest.Records.Add(record);
                result.Succeeded++;

                Log($"OK    [{item.Kind}] {item.Title}");
                Log($"        target: {item.Target}");
                Log($"        {item.Current}  ->  {item.After}");

                if (!record.Restorable) result.Unrestorable.Add(record);

                progress?.Report(new ApplyProgress(item, index, items.Count, "applied", false));
            }
            catch (Exception ex)
            {
                result.Failed++;
                result.Errors.Add($"{item.Title}: {ex.Message}");

                Log($"FAIL  [{item.Kind}] {item.Title}");
                Log($"        {item.Current}");
                Log($"        {ex.Message}");

                progress?.Report(new ApplyProgress(item, index, items.Count, "failed - " + ex.Message, true));
            }

            // Rewritten after every change so an interrupted run stays undoable.
            result.ManifestPath = ManifestStore.Save(manifest);
        }

        // Saved inside the loop after every change; repeated here only for the
        // empty-plan case, where the loop body never ran.
        if (items.Count == 0) result.ManifestPath = ManifestStore.Save(manifest);

        Log(new string('-', 78));
        Log($"Applied {result.Succeeded}, skipped {result.Skipped}, failed {result.Failed}.");

        return result;
    }

    private static UndoRecord ApplyOne(ActionItem item) => item.Kind switch
    {
        ActionKind.Registry => ApplyRegistry(item),
        ActionKind.StorePolicy => ApplyStorePolicy(item),
        ActionKind.Service => ApplyService(item),
        ActionKind.ScheduledTask => ApplyTask(item),
        ActionKind.Appx => ApplyAppx(item),
        ActionKind.ProvisionedAppx => ApplyAppx(item),
        _ => throw new NotSupportedException($"Cannot apply action kind {item.Kind}.")
    };

    private static UndoRecord ApplyRegistry(ActionItem item)
    {
        var tweak = item.Source
            ?? throw new InvalidOperationException("Registry action has no tweak definition.");

        var desiredKind = RegistryOps.ParseKind(tweak.ValueKind);
        var desired = RegistryOps.Coerce(tweak.Value, desiredKind);

        var keyExisted = RegistryOps.KeyExists(tweak.Scope, tweak.Path);
        var (valueExisted, previous, previousKind, _) = RegistryOps.Read(tweak.Scope, tweak.Path, tweak.ValueName);

        RegistryOps.Write(tweak.Scope, tweak.Path, tweak.ValueName, desiredKind, desired);

        return new UndoRecord
        {
            Id = item.Id,
            Title = item.Title,
            Category = item.Category,
            Kind = ActionKind.Registry,
            Scope = tweak.Scope,
            Path = tweak.Path,
            ValueName = tweak.ValueName,
            ValueKind = (previousKind ?? desiredKind).ToString(),
            PreviousValue = RegistryOps.SerializeValue(previous),
            ValueExisted = valueExisted,
            KeyExisted = keyExisted,
            RequiresReboot = tweak.RebootRequired,
            Target = tweak.HumanTarget
        };
    }

    private static UndoRecord ApplyStorePolicy(ActionItem item)
    {
        var path = item.Targets.FirstOrDefault()
            ?? throw new InvalidOperationException("Store policy action has no target.");

        var existed = RegistryOps.KeyExists(RegistryScope.LocalMachine, path);
        RegistryOps.EnsureKey(RegistryScope.LocalMachine, path);

        return new UndoRecord
        {
            Id = item.Id,
            Title = item.Title,
            Category = item.Category,
            Kind = ActionKind.StorePolicy,
            Scope = RegistryScope.LocalMachine,
            Path = path,
            KeyExisted = existed,
            RequiresReboot = item.RequiresReboot,
            Target = path
        };
    }

    private static UndoRecord ApplyService(ActionItem item)
    {
        var name = item.Targets.FirstOrDefault()
            ?? throw new InvalidOperationException("Service action has no target.");

        var before = PowerShell.GetService(name)
            ?? throw new InvalidOperationException($"Service '{name}' was not found.");

        PowerShell.SetServiceStartMode(name, "Disabled");

        return new UndoRecord
        {
            Id = item.Id,
            Title = item.Title,
            Category = item.Category,
            Kind = ActionKind.Service,
            PreviousStartMode = PowerShell.NormalizeStartMode(before.StartMode),
            Target = name
        };
    }

    private static UndoRecord ApplyTask(ActionItem item)
    {
        var tweak = item.Source
            ?? throw new InvalidOperationException("Scheduled task action has no tweak definition.");

        var before = PowerShell.GetScheduledTask(tweak.TaskPath, tweak.TaskName)
            ?? throw new InvalidOperationException($"Task '{tweak.TaskPath}{tweak.TaskName}' was not found.");

        PowerShell.SetScheduledTaskEnabled(tweak.TaskPath, tweak.TaskName, false);

        return new UndoRecord
        {
            Id = item.Id,
            Title = item.Title,
            Category = item.Category,
            Kind = ActionKind.ScheduledTask,
            TaskPath = tweak.TaskPath,
            TaskName = tweak.TaskName,
            PreviousEnabled = before.Enabled,
            Target = tweak.TaskPath + tweak.TaskName
        };
    }

    private static UndoRecord ApplyAppx(ActionItem item)
    {
        var removed = 0;
        var deprovisioned = 0;

        foreach (var target in item.Targets)
        {
            removed += PowerShell.RemoveAppx(target);
            deprovisioned += PowerShell.RemoveProvisionedAppx(target);
        }

        if (removed == 0 && deprovisioned == 0)
        {
            throw new InvalidOperationException(
                "Windows removed nothing for " + string.Join(", ", item.Targets) +
                ". The package may be in use, or already gone.");
        }

        return new UndoRecord
        {
            Id = item.Id,
            Title = item.Title,
            Category = item.Category,
            Kind = item.Kind,
            Restorable = false,
            RequiresReboot = item.RequiresReboot,
            Target = string.Join(", ", item.Targets),
            Note = $"Removed {removed} installed and {deprovisioned} provisioned package(s). " +
                   "Reinstall from the Microsoft Store if you want it back."
        };
    }

    // ------------------------------------------------------------------ undo ---

    public static UndoResult Undo(
        RunManifest manifest,
        IProgress<UndoStep>? progress = null,
        CancellationToken cancellation = default)
    {
        var result = new UndoResult { Total = manifest.Records.Count };

        var records = new List<UndoRecord>(manifest.Records);
        records.Reverse();

        var index = 0;
        foreach (var record in records)
        {
            index++;
            cancellation.ThrowIfCancellationRequested();

            if (!record.Restorable)
            {
                result.Manual.Add($"{record.Title} ({record.Target}). {record.Note}");
                progress?.Report(new UndoStep(index, records.Count, record.Title, "needs manual reinstall", false));
                continue;
            }

            try
            {
                UndoOne(record);
                result.Restored++;
                progress?.Report(new UndoStep(index, records.Count, record.Title, "restored", false));
            }
            catch (Exception ex)
            {
                result.Failed++;
                result.Errors.Add($"{record.Title}: {ex.Message}");
                progress?.Report(new UndoStep(index, records.Count, record.Title, "failed - " + ex.Message, true));
            }
        }

        if (result.Failed == 0) ManifestStore.MarkUndone();

        return result;
    }

    private static void UndoOne(UndoRecord record)
    {
        switch (record.Kind)
        {
            case ActionKind.Registry:
                if (!record.KeyExisted)
                {
                    // We created this key, so remove the whole thing again.
                    RegistryOps.DeleteKey(record.Scope, record.Path);
                }
                else if (record.ValueExisted)
                {
                    var kind = RegistryOps.ParseKind(record.ValueKind);
                    RegistryOps.Write(record.Scope, record.Path, record.ValueName, kind,
                        RegistryOps.DeserializeValue(record.PreviousValue, kind));
                }
                else
                {
                    RegistryOps.DeleteValue(record.Scope, record.Path, record.ValueName);
                }
                break;

            case ActionKind.StorePolicy:
                if (!record.KeyExisted) RegistryOps.DeleteKey(record.Scope, record.Path);
                break;

            case ActionKind.Service:
                if (!string.IsNullOrEmpty(record.PreviousStartMode))
                {
                    PowerShell.SetServiceStartMode(record.Target, record.PreviousStartMode);
                }
                break;

            case ActionKind.ScheduledTask:
                if (record.PreviousEnabled is { } wasEnabled)
                {
                    PowerShell.SetScheduledTaskEnabled(record.TaskPath, record.TaskName, wasEnabled);
                }
                break;

            case ActionKind.Appx:
            case ActionKind.ProvisionedAppx:
                // Unreachable while Restorable is false, which is how these are
                // recorded. Kept explicit so a hand-edited or older manifest
                // reports a clear reason instead of NotSupportedException.
                throw new InvalidOperationException(
                    record.Restorable
                        ? "This manifest claims the package removal can be undone automatically, " +
                          "but AppX packages cannot be reinstated without the original installer. " +
                          $"Reinstall {record.Target} from the Microsoft Store."
                        : $"Reinstall {record.Target} from the Microsoft Store.");

            default:
                throw new NotSupportedException($"Cannot undo action kind {record.Kind}.");
        }
    }

    private static string? SerializeValue(object? value) => value switch
    {
        null => null,
        int i => i.ToString(),
        long l => l.ToString(),
        byte[] bytes => Convert.ToBase64String(bytes),
        string[] items => string.Join("\n", items),
        _ => value.ToString()
    };
}