using Microsoft.Win32;
using System.IO;
using System.Security;

namespace WindowsDebloater.Core;

/// <summary>
/// Thin wrapper over the parts of the registry Appx and service state are exposed
/// through, so each call has a single place to record what was there beforehand.
/// </summary>
public static class RegistryOps
{
    public static RegistryKey Root(RegistryScope scope) =>
        scope == RegistryScope.CurrentUser ? Registry.CurrentUser : Registry.LocalMachine;

    public static string Prefix(RegistryScope scope) =>
        scope == RegistryScope.CurrentUser ? "HKCU" : "HKLM";

    /// <summary>
    /// Some keys are readable but not enumerable even for administrators, for
    /// example the location provider service keys under
    /// SYSTEM\CurrentControlSet\Services. Probing for current state must never
    /// throw, so access failures surface as "unreadable" and are reported in the
    /// preview instead of aborting the whole scan.
    /// </summary>
    private static bool IsAccessProblem(Exception ex) =>
        ex is SecurityException or UnauthorizedAccessException;

    public static RegistryKey? TryOpen(RegistryScope scope, string path, bool writable)
    {
        try
        {
            return Root(scope).OpenSubKey(path, writable);
        }
        catch (Exception ex) when (IsAccessProblem(ex))
        {
            return null;
        }
    }

    public static bool KeyExists(RegistryScope scope, string path) =>
        TryOpen(scope, path, writable: false) is not null;

    /// <summary>
    /// Returns whether the value existed, its value and its kind.
    /// <c>exists: false</c> with <c>readable: true</c> means the key opened fine
    /// but has no such value. <paramref name="readable"/> is false when the key
    /// could not be opened at all, which callers must not confuse with "absent".
    /// </summary>
    public static (bool Exists, object? Value, RegistryValueKind? Kind, bool Readable) Read(
        RegistryScope scope, string path, string valueName)
    {
        using var key = TryOpen(scope, path, writable: false);
        if (key is null) return (false, null, null, Readable: false);

        try
        {
            var present = false;
            foreach (var name in key.GetValueNames())
            {
                if (string.Equals(name, valueName, StringComparison.OrdinalIgnoreCase))
                {
                    present = true;
                    break;
                }
            }

            if (!present) return (false, null, null, Readable: true);

            var value = key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return (true, value, key.GetValueKind(valueName), Readable: true);
        }
        catch (Exception ex) when (IsAccessProblem(ex))
        {
            return (true, null, null, Readable: false);
        }
    }

    /// <summary>
    /// Writes a value. A null <paramref name="value"/> or null
    /// <paramref name="kind"/> means the value is being deleted, which is how
    /// the catalog expresses "remove this setting".
    /// </summary>
    public static void Write(
        RegistryScope scope, string path, string valueName, RegistryValueKind? kind, object? value)
    {
        using var key = CreateSubKey(scope, path)
            ?? throw new InvalidOperationException($"Could not create {Prefix(scope)}\\{path}");

        if (kind is null || value is null)
        {
            key.DeleteValue(valueName, throwOnMissingValue: false);
            return;
        }

        key.SetValue(valueName, value, kind.Value);
    }

    private static RegistryKey? CreateSubKey(RegistryScope scope, string path)
    {
        try
        {
            return Root(scope).CreateSubKey(path, writable: true);
        }
        catch (Exception ex) when (IsAccessProblem(ex))
        {
            return null;
        }
    }

    public static void DeleteValue(RegistryScope scope, string path, string valueName)
    {
        using var key = TryOpen(scope, path, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }

    public static void DeleteKey(RegistryScope scope, string path)
    {
        try
        {
            Root(scope).DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        }
        catch (Exception ex) when (IsAccessProblem(ex))
        {
            // Nothing removable here, which is the same end state as success.
        }
    }

    /// <summary>Creates a key if it is absent. Used for the empty store policy subkeys.</summary>
    public static void EnsureKey(RegistryScope scope, string path)
    {
        using var key = CreateSubKey(scope, path)
            ?? throw new InvalidOperationException($"Could not create {Prefix(scope)}\\{path}");
    }

    /// <summary>
    /// Serializes a registry value into the single-string form the manifest can
    /// hold. MultiString is escaped rather than newline-joined, because a
    /// REG_MULTI_SZ entry may legitimately contain newlines and undo has to
    /// round-trip it exactly.
    /// </summary>
    public static string? SerializeValue(object? value) => value switch
    {
        null => null,
        int i => i.ToString(),
        long l => l.ToString(),
        byte[] bytes => Convert.ToBase64String(bytes),
        string[] items => string.Join("\n", items.Select(EscapeMultiString)),
        _ => value.ToString()
    };

    /// <summary>Reverses <see cref="SerializeValue"/>. Returns null when there is nothing stored.</summary>
    public static object? DeserializeValue(string? raw, RegistryValueKind kind) =>
        kind == RegistryValueKind.MultiString
            ? (raw ?? "").Split('\n').Select(UnescapeMultiString).ToArray()
            : Coerce(raw, kind);

    private static string EscapeMultiString(string item) => item.Replace("\\", "\\\\")
                                                                .Replace("\n", "\\n");

    private static string UnescapeMultiString(string item)
    {
        var result = new System.Text.StringBuilder(item.Length);

        for (var i = 0; i < item.Length; i++)
        {
            if (item[i] != '\\' || i + 1 >= item.Length)
            {
                result.Append(item[i]);
                continue;
            }

            result.Append(item[++i] switch
            {
                'n' => '\n',
                '\\' => '\\',
                _ => item[i]
            });
        }

        return result.ToString();
    }

    public static RegistryValueKind ParseKind(string name) => name switch
    {
        "DWord" => RegistryValueKind.DWord,
        "QWord" => RegistryValueKind.QWord,
        "Binary" => RegistryValueKind.Binary,
        "MultiString" => RegistryValueKind.MultiString,
        "ExpandString" => RegistryValueKind.ExpandString,
        _ => RegistryValueKind.String
    };

    public static object? Coerce(string? raw, RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.DWord => int.Parse(raw ?? "0"),
        RegistryValueKind.QWord => long.Parse(raw ?? "0"),
        RegistryValueKind.Binary => string.IsNullOrEmpty(raw)
            ? Array.Empty<byte>()
            : Convert.FromBase64String(raw),
        RegistryValueKind.MultiString => (raw ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries),
        _ => raw ?? ""
    };

    public static string Describe(object? value) => value switch
    {
        null => "(not set)",
        int i => $"0x{i:X8} ({i})",
        long l => $"0x{l:X16} ({l})",
        byte[] b => $"binary ({b.Length} bytes)",
        string s => $"\"{s}\"",
        _ => value.ToString() ?? ""
    };
}