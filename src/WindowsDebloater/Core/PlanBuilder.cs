using System.Diagnostics;

namespace WindowsDebloater.Core;

/// <summary>Everything discovered about this machine, gathered once per planning pass.</summary>
public sealed class PlanContext
{
    public List<AppxInfo> Installed { get; init; } = [];
    public Dictionary<string, ProvisionedInfo> Provisioned { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, ServiceInfo> Services { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, TaskInfo> Tasks { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public double DiscoverySeconds { get; init; }

    public static PlanContext Discover(IEnumerable<AppEntry> apps, IEnumerable<Tweak> tweaks)
    {
        var stopwatch = Stopwatch.StartNew();

        var installed = PowerShell.GetInstalledAppx();
        var provisioned = PowerShell.GetProvisionedAppx();

        // Warm the service and task state in one PowerShell round trip each,
        // rather than one round trip per item.
        var serviceNames = tweaks
            .Where(t => t.Kind == ActionKind.Service)
            .Select(t => t.Target)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var taskKeys = tweaks
            .Where(t => t.Kind == ActionKind.ScheduledTask)
            .Select(t => t.TaskPath + t.TaskName)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var services = PowerShell.GetServices(serviceNames);
        var tasks = PowerShell.GetScheduledTasks(taskKeys);

        stopwatch.Stop();

        return new PlanContext
        {
            Installed = installed,
            // Grouped rather than ToDictionary: several provisioned entries can
            // share a DisplayName, and a duplicate key would throw.
            Provisioned = provisioned
                .GroupBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase),
            Services = services.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase),
            Tasks = tasks.ToDictionary(t => t.TaskName, StringComparer.Ordinal),
            DiscoverySeconds = stopwatch.Elapsed.TotalSeconds
        };
    }
}

/// <summary>
/// Turns a selection into a list of <see cref="ActionItem"/>s carrying the real
/// current state of every target. This is the dry run: nothing here writes to
/// the system, so the preview the user confirms is exactly what will run.
/// </summary>
public static class PlanBuilder
{
    public const string StorePolicyRoot =
        @"SOFTWARE\Policies\Microsoft\Windows\Appx\RemoveDefaultMicrosoftStorePackages";

    public static List<ActionItem> Build(
        IEnumerable<AppEntry> apps,
        IEnumerable<Tweak> tweaks,
        PlanContext context)
    {
        var items = new List<ActionItem>();

        foreach (var app in apps)
        {
            if (app.Protected) continue;

            var matched = context.Installed.Where(p => app.IsMatching(p.Name)).ToList();
            var provisioned = context.Provisioned.Keys
                .Where(p => app.IsMatching(p))
                .ToList();

            if (matched.Count == 0 && provisioned.Count == 0) continue;

            var targets = matched.Select(p => p.Name)
                .Concat(provisioned)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var detail = matched.Count == 0
                ? "not installed, provisioned only"
                : "installed: " + string.Join(", ", matched.Select(p => $"{p.Name} {p.Version}")) +
                  (provisioned.Count > 0 ? " | provisioned" : "");

            items.Add(new ActionItem
            {
                Id = "app:" + app.AppId,
                Category = app.Category,
                Title = app.Name,
                Description = app.Description,
                Kind = ActionKind.Appx,
                Risk = app.Risk,
                Target = string.Join(", ", targets),
                Targets = targets,
                Current = detail,
                After = "removed",
                RequiresReboot = app.RebootRequired,
                PlanCommand = targets.Count == 1
                    ? $"Remove-AppxPackage -AllUsers -Name {targets[0]}"
                    : $"Remove-AppxPackage -AllUsers / Remove-AppxProvisionedPackage ({targets.Count} packages)",
                App = app
            });
        }

        foreach (var tweak in tweaks)
        {
            switch (tweak.Kind)
            {
                case ActionKind.Registry:
                    items.Add(BuildRegistry(tweak));
                    break;
                case ActionKind.Service:
                    items.Add(BuildService(tweak, context));
                    break;
                case ActionKind.ScheduledTask:
                    items.Add(BuildTask(tweak, context));
                    break;
                case ActionKind.StorePolicy:
                    items.AddRange(BuildStorePolicy(tweak));
                    break;
                case ActionKind.Appx:
                case ActionKind.ProvisionedAppx:
                    items.AddRange(BuildTweakAppx(tweak, context));
                    break;
            }
        }

        return items;
    }

    // -------------------------------------------------------------- registry ---

    private static ActionItem BuildRegistry(Tweak tweak)
    {
        var desiredKind = RegistryOps.ParseKind(tweak.ValueKind);
        var desired = RegistryOps.Coerce(tweak.Value, desiredKind);

        var keyExisted = RegistryOps.KeyExists(tweak.Scope, tweak.Path);
        var (valueExisted, value, _, readable) =
            RegistryOps.Read(tweak.Scope, tweak.Path, tweak.ValueName);
        var alreadyDone = valueExisted && Equals(value, desired);

        // An unreadable key is reported as such rather than as absent, so the
        // preview never claims a setting is unset when we simply could not look.
        var current = !readable
            ? "(unreadable, access denied)"
            : !keyExisted
            ? "(key absent)"
            : valueExisted ? RegistryOps.Describe(value) : "(not set)";

        var location = $"{RegistryOps.Prefix(tweak.Scope)}\\{tweak.Path} :: {tweak.ValueName}";

        return new ActionItem
        {
            Id = tweak.Id,
            Category = tweak.Category,
            Title = tweak.Title,
            Description = tweak.Description,
            Kind = ActionKind.Registry,
            Risk = tweak.Risk,
            Target = location,
            Targets = [location],
            Current = current,
            After = tweak.HumanValue,
            AlreadyDone = alreadyDone,
            RequiresReboot = tweak.RebootRequired,
            PlanCommand = alreadyDone
                ? "no change needed"
                : $"Set-ItemProperty -Path {RegistryOps.Prefix(tweak.Scope)}\\{tweak.Path} -Name {tweak.ValueName} -Value {tweak.Value}" +
                  (keyExisted ? "" : "  [creates key]"),
            Source = tweak
        };
    }

    // --------------------------------------------------------------- service ---

    private static ActionItem BuildService(Tweak tweak, PlanContext context)
    {
        var found = context.Services.TryGetValue(tweak.Target, out var service) && service is not null;
        var alreadyDone = found && service!.StartMode.Equals("Disabled", StringComparison.OrdinalIgnoreCase);

        return new ActionItem
        {
            Id = tweak.Id,
            Category = tweak.Category,
            Title = tweak.Title,
            Description = tweak.Description,
            Kind = ActionKind.Service,
            Risk = tweak.Risk,
            Target = "service " + tweak.Target,
            Targets = [tweak.Target],
            Current = found ? $"{service!.State}, starts {service.StartMode}" : "not present on this system",
            After = "stopped, starts Disabled",
            AlreadyDone = alreadyDone,
            NotApplicable = !found,
            RequiresReboot = tweak.RebootRequired,
            PlanCommand = !found
                ? "nothing to do"
                : alreadyDone ? "no change needed" : $"Set-Service -Name {tweak.Target} -StartupType Disabled",
            Source = tweak
        };
    }

    // -------------------------------------------------------- scheduled task ---

    private static ActionItem BuildTask(Tweak tweak, PlanContext context)
    {
        var key = tweak.TaskPath + tweak.TaskName;
        var found = context.Tasks.TryGetValue(key, out var task) && task is not null;
        var alreadyDone = found && !task!.Enabled;

        return new ActionItem
        {
            Id = tweak.Id,
            Category = tweak.Category,
            Title = tweak.Title,
            Description = tweak.Description,
            Kind = ActionKind.ScheduledTask,
            Risk = tweak.Risk,
            Target = $"task {key}",
            Targets = [key],
            Current = found ? (task!.Enabled ? "enabled" : "disabled") : "not present on this system",
            After = "disabled",
            AlreadyDone = alreadyDone,
            NotApplicable = !found,
            RequiresReboot = tweak.RebootRequired,
            PlanCommand = !found
                ? "nothing to do"
                : alreadyDone ? "no change needed"
                    : $"Disable-ScheduledTask -TaskPath '{tweak.TaskPath}' -TaskName '{tweak.TaskName}'",
            Source = tweak
        };
    }

    // --------------------------------------------------------- store policy ---

    private static IEnumerable<ActionItem> BuildStorePolicy(Tweak tweak)
    {
        foreach (var family in tweak.Target
                     .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var path = StorePolicyRoot + "\\" + family;
            var exists = RegistryOps.KeyExists(RegistryScope.LocalMachine, path);
            var friendly = family.Split('_')[0];

            yield return new ActionItem
            {
                Id = tweak.Id + ":" + family,
                Category = tweak.Category,
                Title = $"Do not provision {friendly}",
                Description = tweak.Description,
                Kind = ActionKind.StorePolicy,
                Risk = tweak.Risk,
                Target = $"HKLM\\{path}",
                Targets = [path],
                Current = exists ? "policy subkey already present" : "(absent)",
                After = "policy subkey present",
                AlreadyDone = exists,
                RequiresReboot = tweak.RebootRequired,
                PlanCommand = exists ? "no change needed" : $"New-Item -Path 'HKLM:\\{path}'",
                Source = tweak
            };
        }
    }

    // ----------------------------------------------------- appx via a tweak ---

    private static IEnumerable<ActionItem> BuildTweakAppx(Tweak tweak, PlanContext context)
    {
        var provisionedOnly = tweak.Kind == ActionKind.ProvisionedAppx;

        var provisionedNames = new HashSet<string>(context.Provisioned.Keys, StringComparer.OrdinalIgnoreCase);
        var installedNames = new HashSet<string>(context.Installed.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);

        var candidates = provisionedOnly
            ? context.Provisioned.Values.Select(p => p.DisplayName)
            : context.Installed.Select(p => p.Name);

        var items = new List<ActionItem>();

        foreach (var target in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var present = provisionedOnly
                ? provisionedNames.Contains(target)
                : installedNames.Contains(target) || provisionedNames.Contains(target);

            if (!present) continue;

            items.Add(new ActionItem
            {
                Id = tweak.Id + ":" + target,
                Category = tweak.Category,
                Title = tweak.Title + " (" + target + ")",
                Description = tweak.Description,
                Kind = tweak.Kind,
                Risk = tweak.Risk,
                Target = target,
                Targets = [target],
                Current = installedNames.Contains(target)
                    ? "installed" + (provisionedNames.Contains(target) ? " and provisioned" : "")
                    : "provisioned",
                After = "removed",
                RequiresReboot = tweak.RebootRequired,
                PlanCommand = provisionedOnly
                    ? $"Remove-AppxProvisionedPackage -Online -DisplayName {target}"
                    : $"Remove-AppxPackage -AllUsers -Name {target}",
                Source = tweak
            });
        }

        return items;
    }
}