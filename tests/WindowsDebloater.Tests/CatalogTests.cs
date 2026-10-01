using WindowsDebloater.Core;

namespace WindowsDebloater.Tests;

/// <summary>
/// Proves the shipped catalog loads and contains no entry that would put a
/// protected package or Windows Update in the blast radius.
/// </summary>
public sealed class CatalogTests
{
    private static readonly Catalog Catalog = Catalog.Load();

    [Fact]
    public void CatalogLoadsWithContent()
    {
        Assert.NotEmpty(Catalog.Apps);
        Assert.NotEmpty(Catalog.Tweaks);
        Assert.False(string.IsNullOrWhiteSpace(Catalog.Source));
    }

    [Fact]
    public void NoCatalogEntryTargetsSysMainOrWindowsUpdate()
    {
        foreach (var tweak in Catalog.Tweaks.Where(t => t.Kind == ActionKind.Service))
        {
            Assert.DoesNotContain("SysMain", tweak.Target, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("wuauserv", tweak.Target, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("UsoSvc", tweak.Target, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("WaaSMedicSvc", tweak.Target, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var app in Catalog.Apps)
        {
            Assert.DoesNotContain(app.Match, m => m.Contains("WindowsUpdate", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(app.Match, m => m.Contains("Windows.SecHealthUI", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void ProtectedAppsAreAlsoDefaultSelectedOff()
    {
        foreach (var app in Catalog.Apps.Where(a => a.Protected))
        {
            Assert.False(app.DefaultSelected,
                $"{app.AppId} is protected and must not be selected by default.");
        }
    }

    [Fact]
    public void EveryEntryHasTheMetadataTheUiBinds()
    {
        foreach (var app in Catalog.Apps)
        {
            Assert.False(string.IsNullOrWhiteSpace(app.AppId), "app id");
            Assert.False(string.IsNullOrWhiteSpace(app.Name), $"{app.AppId} name");
            Assert.False(string.IsNullOrWhiteSpace(app.Category), $"{app.AppId} category");
            Assert.False(string.IsNullOrWhiteSpace(app.Description), $"{app.AppId} description");
            Assert.NotEmpty(app.Match);
        }

        foreach (var tweak in Catalog.Tweaks)
        {
            Assert.False(string.IsNullOrWhiteSpace(tweak.Id), "tweak id");
            Assert.False(string.IsNullOrWhiteSpace(tweak.Title), $"{tweak.Id} title");
            Assert.False(string.IsNullOrWhiteSpace(tweak.Category), $"{tweak.Id} category");

            switch (tweak.Kind)
            {
                case ActionKind.Registry:
                    Assert.False(string.IsNullOrWhiteSpace(tweak.Path), $"{tweak.Id} path");
                    Assert.False(string.IsNullOrWhiteSpace(tweak.ValueKind), $"{tweak.Id} value kind");
                    // A null Value means "delete this value", so only entries that
                    // must name a value need a non-empty ValueName.
                    if (tweak.Value is not null)
                    {
                        Assert.False(string.IsNullOrWhiteSpace(tweak.ValueName), $"{tweak.Id} value name");
                    }
                    break;

                case ActionKind.Service:
                    Assert.False(string.IsNullOrWhiteSpace(tweak.Target), $"{tweak.Id} service name");
                    break;

                case ActionKind.ScheduledTask:
                    Assert.False(string.IsNullOrWhiteSpace(tweak.TaskName), $"{tweak.Id} task name");
                    break;

                case ActionKind.StorePolicy:
                    Assert.False(string.IsNullOrWhiteSpace(tweak.Target), $"{tweak.Id} store policy families");
                    break;
            }
        }
    }

    [Fact]
    public void TweakIdsAreUnique()
    {
        var duplicates = Catalog.Tweaks
            .GroupBy(t => t.Id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void RegistryKindsAreAllRecognised()
    {
        var known = new[] { "DWord", "QWord", "String", "ExpandString", "MultiString", "Binary" };

        foreach (var tweak in Catalog.Tweaks.Where(t => t.Kind == ActionKind.Registry))
        {
            Assert.Contains(tweak.ValueKind, known);
        }
    }

    [Fact]
    public void RiskFlagsAreConsistent()
    {
        // A wildcard entry can sweep up packages beyond its own app family, so it
        // must be at least moderate risk.
        foreach (var app in Catalog.Apps.Where(a => a.AllowWildcard))
        {
            Assert.True(app.Risk >= RiskLevel.Moderate,
                $"{app.AppId} allows wildcards but is only {app.Risk} risk.");
        }
    }

    [Fact]
    public void HumanValueFormatsNumericKinds()
    {
        var dword = new Tweak { ValueKind = "DWord", Value = "1" };
        var qword = new Tweak { ValueKind = "QWord", Value = "1" };
        var del = new Tweak { ValueKind = "String", Value = null };

        Assert.Equal("0x00000001 (1)", dword.HumanValue);
        Assert.Equal("0x0000000000000001 (1)", qword.HumanValue);
        Assert.Equal("(deleted)", del.HumanValue);
    }
}