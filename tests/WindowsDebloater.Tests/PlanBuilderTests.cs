using WindowsDebloater.Core;

namespace WindowsDebloater.Tests;

/// <summary>
/// Plan building is the dry run the user approves, so these tests feed a
/// synthetic <see cref="PlanContext"/> and never touch the real machine.
/// </summary>
public sealed class PlanBuilderTests
{
    private static AppxInfo Appx(string name) =>
        new(name, name + "_1.0.0.0_x64", name + "_abc", "1.0.0.0", false, false);

    private static PlanContext Context(
        IEnumerable<AppxInfo>? installed = null,
        Dictionary<string, ProvisionedInfo>? provisioned = null,
        Dictionary<string, ServiceInfo>? services = null,
        Dictionary<string, TaskInfo>? tasks = null) => new()
        {
            Installed = [.. installed ?? []],
            Provisioned = provisioned ?? new Dictionary<string, ProvisionedInfo>(StringComparer.OrdinalIgnoreCase),
            Services = services ?? new Dictionary<string, ServiceInfo>(StringComparer.OrdinalIgnoreCase),
            Tasks = tasks ?? new Dictionary<string, TaskInfo>(StringComparer.Ordinal)
        };

    private static AppEntry App(string appId, string name, params string[] matches) => new()
    {
        AppId = appId,
        Name = name,
        Category = "Bloat",
        Description = "test app",
        Match = [.. matches],
        DefaultSelected = true
    };

    [Fact]
    public void ProtectedApp_IsNeverIncludedInPlan()
    {
        var app = App("prot", "Protected App", "Some.Pkg");
        app.Protected = true;

        var plan = PlanBuilder.Build([app], [], Context(installed: [Appx("Some.Pkg")]));

        Assert.Empty(plan);
    }

    [Fact]
    public void AbsentApp_IsOmittedEntirely()
    {
        var plan = PlanBuilder.Build([App("absent", "Absent", "Not.Installed")], [], Context());

        Assert.Empty(plan);
    }

    [Fact]
    public void ProvisionedOnlyApp_IsStillIncluded()
    {
        var context = Context(provisioned: new Dictionary<string, ProvisionedInfo>(StringComparer.OrdinalIgnoreCase)
        {
            ["Only.Provisioned"] = new("Only.Provisioned", "Only.Provisioned_1.0.0.0_x64")
        });

        var plan = PlanBuilder.Build([App("p", "Provisioned App", "Only.Provisioned")], [], context);

        var item = Assert.Single(plan);
        Assert.Equal("not installed, provisioned only", item.Current);
        Assert.False(item.NotApplicable);
    }

    [Fact]
    public void EveryPlannedItemCarriesPreviewableText()
    {
        var context = Context(
            installed: [Appx("Contoso.Bloat")],
            services: new Dictionary<string, ServiceInfo>(StringComparer.OrdinalIgnoreCase)
            {
                ["MapsBroker"] = new("MapsBroker", "Running", "Auto")
            },
            tasks: new Dictionary<string, TaskInfo>(StringComparer.Ordinal)
            {
                ["Cleanup"] = new("Cleanup", true)
            });

        var tweaks = new List<Tweak>
        {
            new()
            {
                Id = "svc", Category = "Services", Title = "Maps broker", Kind = ActionKind.Service,
                Target = "MapsBroker", Risk = RiskLevel.Low
            },
            new()
            {
                Id = "task", Category = "Tasks", Title = "Cleanup task", Kind = ActionKind.ScheduledTask,
                TaskPath = "\\Contoso\\", TaskName = "Cleanup", Risk = RiskLevel.Low
            }
        };

        var plan = PlanBuilder.Build([App("a", "Bloat app", "Contoso.Bloat")], tweaks, context);

        Assert.Equal(3, plan.Count);

        foreach (var item in plan)
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Title), $"{item.Id} title");
            Assert.False(string.IsNullOrWhiteSpace(item.Target), $"{item.Id} target");
            Assert.False(string.IsNullOrWhiteSpace(item.Current), $"{item.Id} current");
            Assert.False(string.IsNullOrWhiteSpace(item.OutcomeText), $"{item.Id} outcome");
            Assert.False(string.IsNullOrWhiteSpace(item.PlanCommand), $"{item.Id} command");
        }
    }

    [Fact]
    public void MissingService_IsMarkedNotApplicableAndDoesNotClaimARestart()
    {
        var tweak = new Tweak
        {
            Id = "svc", Category = "Services", Title = "Absent service", Kind = ActionKind.Service,
            Target = "NoSuchService", RebootRequired = true
        };

        var plan = PlanBuilder.Build([], [tweak], Context());
        var item = Assert.Single(plan);

        Assert.True(item.NotApplicable);
        Assert.Equal("nothing to do", item.PlanCommand);
        Assert.Equal("not present on this system", item.OutcomeText);
    }

    [Fact]
    public void EnabledTask_IsPlannedButDisabledTask_IsAlreadyDone()
    {
        var tweak = new Tweak
        {
            Id = "task", Category = "Tasks", Title = "Cleanup", Kind = ActionKind.ScheduledTask,
            TaskPath = "\\Contoso\\", TaskName = "Cleanup"
        };

        var enabled = PlanBuilder.Build([], [tweak], Context(tasks:
            new Dictionary<string, TaskInfo>(StringComparer.Ordinal)
            {
                ["\\Contoso\\Cleanup"] = new("Cleanup", true)
            }));

        var disabled = PlanBuilder.Build([], [tweak], Context(tasks:
            new Dictionary<string, TaskInfo>(StringComparer.Ordinal)
            {
                ["\\Contoso\\Cleanup"] = new("Cleanup", false)
            }));

        Assert.False(Assert.Single(enabled).AlreadyDone);
        Assert.True(Assert.Single(disabled).AlreadyDone);
    }

    [Fact]
    public void RebootFlagSurfacesInOutcomeOnlyWhenAChangeWillHappen()
    {
        var pending = new ActionItem { After = "removed", RequiresReboot = true };
        var done = new ActionItem { After = "removed", RequiresReboot = true, AlreadyDone = true };
        var absent = new ActionItem { After = "removed", RequiresReboot = true, NotApplicable = true };
        var immediate = new ActionItem { After = "removed" };

        Assert.Equal("removed, after restart", pending.OutcomeText);
        Assert.Equal("no change needed", done.OutcomeText);
        Assert.Equal("not present on this system", absent.OutcomeText);
        Assert.Equal("removed", immediate.OutcomeText);
    }

    [Fact]
    public void WildcardMatchingRequiresExplicitOptIn()
    {
        var strict = App("s", "Strict", "Microsoft.*");
        var loose = App("l", "Loose", "Microsoft.*");
        loose.AllowWildcard = true;

        Assert.False(strict.IsMatching("Microsoft.WindowsStore"));
        Assert.True(loose.IsMatching("Microsoft.WindowsStore"));
    }
}