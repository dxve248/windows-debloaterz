using WindowsDebloater.Core;

namespace WindowsDebloater.Tests;

public sealed class ServiceStartModeTests
{
    /// <summary>
    /// Win32_Service reports "Auto", "Manual", "Disabled", "Boot" and "System",
    /// but it also reports "Start" and "Running" depending on the state of the
    /// service. Those all mean Automatic and must be sent back as such, or the
    /// undo write fails.
    /// </summary>
    [Theory]
    [InlineData("Auto", "Automatic")]
    [InlineData("Automatic", "Automatic")]
    [InlineData("Start", "Automatic")]
    [InlineData("Running", "Automatic")]
    [InlineData("Manual", "Manual")]
    [InlineData("Demand", "Manual")]
    [InlineData("Disabled", "Disabled")]
    [InlineData("Boot", "Boot")]
    [InlineData("System", "System")]
    [InlineData("  Auto  ", "Automatic")]
    public void NormalizeStartMode_MapsWin32Vocabulary(string input, string expected)
    {
        Assert.Equal(expected, PowerShell.NormalizeStartMode(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Nonsense")]
    public void NormalizeStartMode_RejectsUnknownVocabulary(string input)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PowerShell.NormalizeStartMode(input));
    }
}

public sealed class ManifestRoundTripTests
{
    [Fact]
    public void ManifestWithMixedRecordsSurvivesSerialization()
    {
        var manifest = new RunManifest
        {
            Timestamp = "20260101-000000",
            Machine = "TESTHOST",
            User = "tester",
            RestorePointCreated = true,
            RestorePointDescription = "Before_Debloat_20260101-000000",
            Records =
            [
                new UndoRecord
                {
                    Kind = ActionKind.Registry,
                    Scope = RegistryScope.CurrentUser,
                    Path = @"Software\Thing",
                    ValueName = "Multi",
                    ValueKind = "MultiString",
                    PreviousValue = RegistryOps.SerializeValue(new[] { "a\nb", "c" }),
                    ValueExisted = true,
                    KeyExisted = true,
                    RequiresReboot = true
                },
                new UndoRecord
                {
                    Kind = ActionKind.Appx,
                    Target = "Contoso.Bloat",
                    Restorable = false,
                    Note = "Reinstall from the Store."
                }
            ]
        };

        var json = System.Text.Json.JsonSerializer.Serialize(manifest);
        var restored = System.Text.Json.JsonSerializer.Deserialize<RunManifest>(json,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(restored);
        Assert.Equal(2, restored!.Records.Count);
        Assert.Equal(1, restored.RestorableCount);
        Assert.Equal(1, restored.ManualCount);

        var registry = restored.Records[0];
        Assert.True(registry.RequiresReboot);
        Assert.Equal(new[] { "a\nb", "c" },
            Assert.IsType<string[]>(RegistryOps.DeserializeValue(registry.PreviousValue, RegistryValueKindOf(registry))));

        Assert.False(restored.Records[1].Restorable);
    }

    private static Microsoft.Win32.RegistryValueKind RegistryValueKindOf(UndoRecord record) =>
        (Microsoft.Win32.RegistryValueKind)System.Enum.Parse(
            typeof(Microsoft.Win32.RegistryValueKind), record.ValueKind);
}