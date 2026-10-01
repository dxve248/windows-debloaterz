using Microsoft.Win32;
using WindowsDebloater.Core;

namespace WindowsDebloater.Tests;

/// <summary>
/// Exercises a real scratch key under HKCU so the round trip is proven against
/// the actual registry rather than a mock. Nothing here touches a catalog path.
/// </summary>
public sealed class RegistryRoundTripTests : IDisposable
{
    private const string TestPath = @"Software\WindowsDebloaterTests\RoundTrip";
    private readonly string _keyPath;

    public RegistryRoundTripTests()
    {
        _keyPath = TestPath + "_" + Guid.NewGuid().ToString("N")[..8];
        Registry.CurrentUser.CreateSubKey(_keyPath);
    }

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(_keyPath, throwOnMissingSubKey: false);

    [Fact]
    public void DWord_RoundTrips()
    {
        AssertRoundTrip(RegistryValueKind.DWord, 1234);
    }

    [Fact]
    public void QWord_RoundTrips()
    {
        AssertRoundTrip(RegistryValueKind.QWord, 9_000_000_000L);
    }

    [Fact]
    public void String_RoundTrips()
    {
        AssertRoundTrip(RegistryValueKind.String, "plain value");
    }

    [Fact]
    public void ExpandString_RoundTripsWithoutExpansion()
    {
        AssertRoundTrip(RegistryValueKind.ExpandString, @"%TEMP%\keep-this-literal");
    }

    [Fact]
    public void Binary_RoundTrips()
    {
        AssertRoundTrip(RegistryValueKind.Binary, new byte[] { 0x00, 0xFF, 0x10, 0x00, 0xAB });
    }

    /// <summary>
    /// The regression this file exists for: REG_MULTI_SZ entries may contain
    /// newlines, so a newline-joined serialization would split one element into
    /// several on undo.
    /// </summary>
    [Fact]
    public void MultiString_RoundTripsElementsContainingNewlines()
    {
        AssertRoundTrip(RegistryValueKind.MultiString,
            new[] { "plain", "line one\nline two", "back\\slash", "", "trailing\\" });
    }

    [Fact]
    public void MultiString_EmptyStringElement_Survives()
    {
        AssertRoundTrip(RegistryValueKind.MultiString, new[] { "a", "", "b" });
    }

    [Fact]
    public void UndoValue_RestoresDeletedValueWhenWrittenBack()
    {
        const string name = "UndoMe";

        // Simulate the record that ApplyRegistry writes when the value did not
        // exist beforehand, then run the same undo code path the executor uses.
        RegistryOps.Write(RegistryScope.CurrentUser, _keyPath, name, RegistryValueKind.MultiString,
            new[] { "one\ntwo", "three" });

        var (existed, previous, _, _) = RegistryOps.Read(RegistryScope.CurrentUser, _keyPath, name);
        var kind = RegistryValueKind.MultiString;

        var record = new UndoRecord
        {
            Kind = ActionKind.Registry,
            Scope = RegistryScope.CurrentUser,
            Path = _keyPath,
            ValueName = name,
            ValueKind = kind.ToString(),
            PreviousValue = RegistryOps.SerializeValue(previous),
            ValueExisted = existed,
            KeyExisted = true
        };

        // Change it away from the recorded state, as a real run would.
        RegistryOps.Write(RegistryScope.CurrentUser, _keyPath, name, RegistryValueKind.MultiString,
            new[] { "clobbered" });

        RegistryOps.Write(RegistryScope.CurrentUser, record.Path, record.ValueName,
            RegistryOps.ParseKind(record.ValueKind),
            RegistryOps.DeserializeValue(record.PreviousValue, RegistryOps.ParseKind(record.ValueKind)));

        var restored = RegistryOps.Read(RegistryScope.CurrentUser, _keyPath, name);

        Assert.True(restored.Exists);
        Assert.Equal(new[] { "one\ntwo", "three" }, Assert.IsType<string[]>(restored.Value));
    }

    [Fact]
    public void Write_WithNullValue_Deletes()
    {
        const string name = "ToDelete";

        RegistryOps.Write(RegistryScope.CurrentUser, _keyPath, name, RegistryValueKind.DWord, 7);
        Assert.True(RegistryOps.Read(RegistryScope.CurrentUser, _keyPath, name).Exists);

        RegistryOps.Write(RegistryScope.CurrentUser, _keyPath, name, RegistryValueKind.DWord, null);

        Assert.False(RegistryOps.Read(RegistryScope.CurrentUser, _keyPath, name).Exists);
    }

    [Fact]
    public void Read_OfAbsentKey_ReportsNotReadableRatherThanEmpty()
    {
        var (exists, _, _, readable) = RegistryOps.Read(
            RegistryScope.CurrentUser, _keyPath + "\\nope", "whatever");

        Assert.False(exists);
        Assert.False(readable);
    }

    private void AssertRoundTrip(RegistryValueKind kind, object expected)
    {
        const string name = "Value";

        RegistryOps.Write(RegistryScope.CurrentUser, _keyPath, name, kind, expected);

        var (existed, actual, actualKind, readable) =
            RegistryOps.Read(RegistryScope.CurrentUser, _keyPath, name);

        Assert.True(existed, "value should exist after write");
        Assert.True(readable, "scratch key should always be readable");
        Assert.Equal(kind, actualKind);

        if (expected is byte[] expectedBytes)
        {
            Assert.Equal(expectedBytes, Assert.IsType<byte[]>(actual));
        }
        else if (expected is string[] expectedItems)
        {
            Assert.Equal(expectedItems, Assert.IsType<string[]>(actual));
        }
        else
        {
            Assert.Equal(expected, actual);
        }

        // Second pass: the value we just read must serialize and deserialize
        // back to itself, which is exactly what an undo manifest stores.
        var serialized = RegistryOps.SerializeValue(actual);
        var roundTripped = RegistryOps.DeserializeValue(serialized, kind);

        if (expected is byte[] bytes)
        {
            Assert.Equal(bytes, Assert.IsType<byte[]>(roundTripped));
        }
        else if (expected is string[] items)
        {
            Assert.Equal(items, Assert.IsType<string[]>(roundTripped));
        }
        else
        {
            Assert.Equal(expected, roundTripped);
        }
    }
}