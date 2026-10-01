using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace WindowsDebloater.Core;

public sealed record AppxInfo(
    string Name,
    string PackageFullName,
    string PackageFamilyName,
    string Version,
    bool IsFramework,
    bool NonRemovable);

public sealed record ProvisionedInfo(string DisplayName, string PackageName);

public sealed record ServiceInfo(string Name, string State, string StartMode);

public sealed record TaskInfo(string TaskName, bool Enabled);

/// <summary>
/// Appx packages, services and scheduled tasks are only reachable through
/// PowerShell's management cmdlets, so those calls are funnelled through here.
/// Scripts are written to a temp file rather than passed on the command line to
/// avoid quoting problems, and the temp file is always cleaned up.
/// </summary>
public static class PowerShell
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    private static string Run(string script, int timeoutSeconds = 420)
    {
        var temp = Path.Combine(Path.GetTempPath(), $"debloater-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(temp, script);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{temp}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Could not start powershell.exe.");

            // Both streams are drained before waiting so a large payload cannot deadlock us.
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(timeoutSeconds * 1000))
            {
                try { process.Kill(true); } catch { /* best effort */ }
                throw new TimeoutException(
                    $"The PowerShell operation exceeded {timeoutSeconds} seconds and was stopped.");
            }

            var output = stdout.GetAwaiter().GetResult();
            var error = stderr.GetAwaiter().GetResult();

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"PowerShell exited with code {process.ExitCode}: {error.Trim()}");
            }

            return output;
        }
        finally
        {
            try { File.Delete(temp); } catch { /* best effort */ }
        }
    }

    private static List<T> ParseList<T>(string json)
    {
        json = json.Trim();
        if (json.Length == 0 || json == "null") return [];

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
        {
            return [.. root.EnumerateArray().Select(e => e.Deserialize<T>(JsonOptions)!)];
        }

        return [root.Deserialize<T>(JsonOptions)!];
    }

    private static string Escape(string value) => value.Replace("'", "''");

    // ------------------------------------------------------------------ appx ---

    public static List<AppxInfo> GetInstalledAppx() =>
        ParseList<AppxInfo>(Run($@"
$ErrorActionPreference = 'Stop'
$items = Get-AppxPackage -AllUsers |
    Select-Object Name, PackageFullName, PackageFamilyName, Version, IsFramework, NonRemovable
ConvertTo-Json -InputObject @($items) -Compress -Depth 3
"));

    public static List<ProvisionedInfo> GetProvisionedAppx() =>
        ParseList<ProvisionedInfo>(Run($@"
$ErrorActionPreference = 'Stop'
$items = Get-AppxProvisionedPackage -Online |
    Select-Object DisplayName, PackageName
ConvertTo-Json -InputObject @($items) -Compress -Depth 3
"));

    public static int RemoveAppx(string packageName) =>
        int.TryParse(Run($@"
$ErrorActionPreference = 'Continue'
$removed = 0
Get-AppxPackage -AllUsers -Name '{Escape(packageName)}' -ErrorAction SilentlyContinue |
    ForEach-Object {{
        try {{ $_ | Remove-AppxPackage -AllUsers -ErrorAction Stop; $removed++ }} catch {{ }}
    }}
$removed
").Trim(), out var count) ? count : 0;

    public static int RemoveProvisionedAppx(string displayName) =>
        int.TryParse(Run($@"
$ErrorActionPreference = 'Continue'
$removed = 0
Get-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue |
    Where-Object {{ $_.DisplayName -eq '{Escape(displayName)}' }} |
    ForEach-Object {{
        try {{ $_ | Remove-AppxProvisionedPackage -Online -ErrorAction Stop; $removed++ }} catch {{ }}
    }}
$removed
").Trim(), out var count) ? count : 0;

    // --------------------------------------------------------------- service ---

    public static ServiceInfo? GetService(string name)
    {
        var json = Run($@"
$s = Get-CimInstance -ClassName Win32_Service -Filter ""Name='{Escape(name)}'"" -ErrorAction SilentlyContinue
if ($s) {{
    ConvertTo-Json -InputObject @($s | Select-Object Name, State, StartMode) -Compress -Depth 3
}}
").Trim();

        if (json.Length == 0 || json == "null") return null;
        return ParseList<ServiceInfo>(json).FirstOrDefault();
    }

    /// <summary>
    /// Sets the startup type and stops the service. The type is normalised first:
    /// Win32_Service.StartMode reports "Auto", while Set-Service -StartupType
    /// wants "Automatic". PowerShell would coerce the short form, but relying on
    /// that for an undo path would be fragile.
    /// </summary>
    public static void SetServiceStartMode(string name, string startupType)
    {
        var normalized = NormalizeStartMode(startupType);

        Run($@"
$ErrorActionPreference = 'Stop'
Set-Service -Name '{Escape(name)}' -StartupType {normalized} -ErrorAction Stop
Stop-Service -Name '{Escape(name)}' -Force -ErrorAction SilentlyContinue
", 120);
    }

    /// <summary>Maps the Win32_Service StartMode vocabulary onto ServiceStartMode names.</summary>
    public static string NormalizeStartMode(string startMode) => startMode.Trim() switch
    {
        "Auto" or "Automatic" or "Start" or "Running" => "Automatic",
        "Manual" or "Demand" => "Manual",
        "Disabled" => "Disabled",
        "Boot" => "Boot",
        "System" => "System",
        _ => throw new ArgumentOutOfRangeException(
            nameof(startMode), startMode, "Unrecognised service start mode.")
    };

    /// <summary>Reads every named service in a single query.</summary>
    public static List<ServiceInfo> GetServices(IReadOnlyCollection<string> names)
    {
        if (names.Count == 0) return [];

        var list = string.Join(",", names.Select(n => $"'{Escape(n)}'"));

        return ParseList<ServiceInfo>(Run($@"
$wanted = @({list})
$s = Get-CimInstance -ClassName Win32_Service -ErrorAction SilentlyContinue |
     Where-Object {{ $wanted -contains $_.Name }} |
     Select-Object Name, State, StartMode
ConvertTo-Json -InputObject @($s) -Compress -Depth 3
"));
    }

    // --------------------------------------------------------- scheduledtask ---

    public static TaskInfo? GetScheduledTask(string taskPath, string taskName)
    {
        var json = Run($@"
$t = Get-ScheduledTask -TaskPath '{Escape(taskPath)}' -TaskName '{Escape(taskName)}' -ErrorAction SilentlyContinue
if ($t) {{
    ConvertTo-Json -InputObject @(
        [pscustomobject]@{{ TaskName = $t.TaskName; Enabled = [bool]$t.Settings.Enabled }}
    ) -Compress -Depth 3
}}
", 120).Trim();

        if (json.Length == 0 || json == "null") return null;
        return ParseList<TaskInfo>(json).FirstOrDefault();
    }

    public static void SetScheduledTaskEnabled(string taskPath, string taskName, bool enabled) =>
        Run($@"
$ErrorActionPreference = 'Stop'
$verb = if ({ToPsBool(enabled)}) {{ 'Enable' }} else {{ 'Disable' }}
& ""$verb-ScheduledTask"" -TaskPath '{Escape(taskPath)}' -TaskName '{Escape(taskName)}' -ErrorAction Stop | Out-Null
", 120);

    private static string ToPsBool(bool value) => value ? "$true" : "$false";

    /// <summary>Reads every named task in a single enumeration. Key is path + name.</summary>
    public static List<TaskInfo> GetScheduledTasks(IReadOnlyCollection<string> keys)
    {
        if (keys.Count == 0) return [];

        var list = string.Join(",", keys.Select(k => $"'{Escape(k)}'"));

        return ParseList<TaskInfo>(Run($@"
$wanted = @({list})
$t = Get-ScheduledTask -ErrorAction SilentlyContinue |
     Where-Object {{ $wanted -contains ($_.TaskPath + $_.TaskName) }} |
     ForEach-Object {{
         [pscustomobject]@{{ TaskName = $_.TaskPath + $_.TaskName; Enabled = [bool]$_.Settings.Enabled }}
     }}
ConvertTo-Json -InputObject @($t) -Compress -Depth 3
"));
    }

    // ---------------------------------------------------------- restore point ---

    /// <summary>
    /// Creates a system restore point, temporarily lifting the once-per-24-hour
    /// throttle so the tool can be re-run within a day. The original frequency is
    /// always put back.
    /// </summary>
    public static void CreateRestorePoint(string description) =>
        Run($@"
$ErrorActionPreference = 'Stop'
$key = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore'
$previous = (Get-ItemProperty -Path $key -Name 'SystemRestorePointCreationFrequency' -ErrorAction SilentlyContinue).SystemRestorePointCreationFrequency
try {{
    Set-ItemProperty -Path $key -Name 'SystemRestorePointCreationFrequency' -Value 0 -ErrorAction SilentlyContinue
    $drive = ""$($env:SystemDrive)\""
    Enable-ComputerRestore -Drive $drive -ErrorAction SilentlyContinue
    Checkpoint-Computer -Description '{Escape(description)}' -RestorePointType 'MODIFY_SETTINGS' -ErrorAction Stop
}}
finally {{
    if ($null -ne $previous) {{
        Set-ItemProperty -Path $key -Name 'SystemRestorePointCreationFrequency' -Value $previous -ErrorAction SilentlyContinue
    }}
}}
", 300);

    // --------------------------------------------------------- system context ---

    public static bool IsAdministrator()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    /// <summary>Copies text to the clipboard. The WPF clipboard needs an STA thread.</summary>
    public static void CopyToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        var thread = new Thread(() =>
        {
            try
            {
                System.Windows.Clipboard.SetText(text);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Clipboard failed: {ex.Message}");
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
    }
}