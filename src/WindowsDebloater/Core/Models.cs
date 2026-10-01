using System.Text.Json.Serialization;

namespace WindowsDebloater.Core;

public enum RiskLevel
{
    Safe,
    Low,
    Moderate,
    High
}

public enum ActionKind
{
    Registry,
    Appx,
    ProvisionedAppx,
    Service,
    ScheduledTask,
    StorePolicy
}

public enum RegistryScope
{
    CurrentUser,
    LocalMachine
}

// ---------------------------------------------------------------- catalog ---

public sealed class Tweak
{
    public string Id { get; set; } = "";
    public string Category { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ActionKind Kind { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RegistryScope Scope { get; set; } = RegistryScope.CurrentUser;

    public string Path { get; set; } = "";
    public string ValueName { get; set; } = "";
    public string ValueKind { get; set; } = "DWord";
    public string? Value { get; set; }

    public string Target { get; set; } = "";
    public string TaskPath { get; set; } = "";
    public string TaskName { get; set; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RiskLevel Risk { get; set; } = RiskLevel.Safe;

    public bool DefaultOn { get; set; }

    /// <summary>
    /// True when the change only takes full effect after a restart. Opt-in per
    /// entry, because most of these changes apply immediately and saying
    /// otherwise would train the user to ignore the warning.
    /// </summary>
    public bool RebootRequired { get; set; }

    public string HumanTarget => Kind switch
    {
        ActionKind.Registry => $"{Scope}\\{Path}" + (ValueName.Length > 0 ? $" :: {ValueName}" : ""),
        ActionKind.Service => $"service {Target}",
        ActionKind.ScheduledTask => $"task {TaskPath}{TaskName}",
        ActionKind.StorePolicy => $"store policy for {Target}",
        _ => Target
    };

    public string HumanValue => ValueKind switch
    {
        "DWord" => $"0x{int.Parse(Value ?? "0"):X8} ({Value})",
        "QWord" => $"0x{long.Parse(Value ?? "0"):X16} ({Value})",
        _ => Value ?? "(deleted)"
    };
}

public sealed class AppEntry
{
    public string AppId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Match { get; set; } = [];

    public bool DefaultSelected { get; set; }

    /// <summary>True when removing this app only fully takes effect after a restart.</summary>
    public bool RebootRequired { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RiskLevel Risk { get; set; } = RiskLevel.Safe;

    public bool Protected { get; set; }

    /// <summary>
    /// Opt-in only. When false the package name must match exactly, so a broad
    /// pattern can never silently remove an unrelated package.
    /// </summary>
    public bool AllowWildcard { get; set; }

    public bool IsMatching(string packageName)
    {
        foreach (var pattern in Match)
        {
            if (AllowWildcard)
            {
                if (Wildcard(pattern, packageName)) return true;
            }
            else if (string.Equals(pattern, packageName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static bool Wildcard(string pattern, string value)
    {
        if (pattern.StartsWith('*') && pattern.EndsWith('*') && pattern.Length > 2)
        {
            return value.Contains(pattern[1..^1], StringComparison.OrdinalIgnoreCase);
        }
        if (pattern.StartsWith('*'))
            return value.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase);
        if (pattern.EndsWith('*'))
            return value.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase);
        return string.Equals(pattern, value, StringComparison.OrdinalIgnoreCase);
    }
}

// ------------------------------------------------------------------- plan ---

public sealed class ActionItem
{
    public string Id { get; set; } = "";
    public string Category { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ActionKind Kind { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RiskLevel Risk { get; set; } = RiskLevel.Safe;

    public string Target { get; set; } = "";
    public string Current { get; set; } = "";
    public string After { get; set; } = "";

    /// <summary>
    /// Every distinct thing this action touches. Usually one entry; an app
    /// removal may cover several packages and a store policy expands to one
    /// policy subkey per package family.
    /// </summary>
    public List<string> Targets { get; set; } = [];

    /// <summary>The catalog tweak this came from, when applicable.</summary>
    public Tweak? Source { get; set; }

    /// <summary>The catalog app this came from, when applicable.</summary>
    public AppEntry? App { get; set; }

    /// <summary>Set when the item is already in the requested state.</summary>
    public bool AlreadyDone { get; set; }

    /// <summary>Set when the target does not exist on this machine.</summary>
    public bool NotApplicable { get; set; }

    /// <summary>Set from the catalog entry; surfaced in the preview and carried into the undo record.</summary>
    public bool RequiresReboot { get; set; }

    public string PlanCommand { get; set; } = "";

    public string RiskLabel => Risk switch
    {
        RiskLevel.Moderate => "moderate risk",
        RiskLevel.High => "high risk",
        _ => ""
    };

    public bool HasRisk => Risk >= RiskLevel.Moderate;

    /// <summary>What this action will actually accomplish when it runs.</summary>
    public string OutcomeText
    {
        get
        {
            var baseText = NotApplicable ? "not present on this system"
                : AlreadyDone ? "no change needed"
                : After;

            return RequiresReboot && !AlreadyDone && !NotApplicable
                ? baseText + ", after restart"
                : baseText;
        }
    }

    public bool OutcomeIsNoOp => NotApplicable || AlreadyDone;
}

// ------------------------------------------------------------------- undo ---

public sealed class UndoRecord
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Category { get; set; } = "";

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ActionKind Kind { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public RegistryScope Scope { get; set; } = RegistryScope.CurrentUser;

    public string Path { get; set; } = "";
    public string ValueName { get; set; } = "";
    public string ValueKind { get; set; } = "DWord";
    public string? PreviousValue { get; set; } = null;
    public bool ValueExisted { get; set; }
    public bool KeyExisted { get; set; }

    /// <summary>Service name, or the package that was removed.</summary>
    public string Target { get; set; } = "";
    public string TaskPath { get; set; } = "";
    public string TaskName { get; set; } = "";

    public string? PreviousStartMode { get; set; }
    public bool? PreviousEnabled { get; set; }

    /// <summary>False for Appx removals, which cannot be reliably reinstated.</summary>
    public bool Restorable { get; set; } = true;

    /// <summary>
    /// True when this change only takes full effect after a restart. Set from
    /// the catalog's risk metadata rather than inferred from the record count.
    /// </summary>
    public bool RequiresReboot { get; set; }

    public string? Note { get; set; }
}

public sealed class RunManifest
{
    public string Version { get; set; } = "1";
    public string Timestamp { get; set; } = "";
    public string Machine { get; set; } = "";
    public string User { get; set; } = "";
    public bool RestorePointCreated { get; set; }
    public string RestorePointDescription { get; set; } = "";
    public List<UndoRecord> Records { get; set; } = [];

    public int RestorableCount => Records.Count(r => r.Restorable);
    public int ManualCount => Records.Count(r => !r.Restorable);
}