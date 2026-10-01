using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using WindowsDebloater.Core;

namespace WindowsDebloater.ViewModels;

public sealed class PlanReadyArgs(IReadOnlyList<ActionItem> items, Func<IReadOnlyList<ActionItem>, Task> commit) : EventArgs
{
    public IReadOnlyList<ActionItem> Items { get; } = items;
    public Func<IReadOnlyList<ActionItem>, Task> Commit { get; } = commit;
}

public sealed class RunFinishedArgs(RunResult result) : EventArgs
{
    public RunResult Result { get; } = result;
}

public sealed class UndoFinishedArgs(UndoResult result, string source) : EventArgs
{
    public UndoResult Result { get; } = result;
    public string Source { get; } = source;
}

public sealed class MainViewModel : ObservableObject
{
    private readonly Catalog? _catalog;
    private readonly string _loadError;
    private PlanContext? _context;

    /// <summary>When <see cref="_context"/> was captured, used to detect a stale preview.</summary>
    private DateTimeOffset _contextScannedAt = DateTimeOffset.MinValue;

    private readonly StringBuilder _log = new();

    /// <summary>How old the cached scan may be before a preview re-reads state.</summary>
    private static readonly TimeSpan ScanFreshness = TimeSpan.FromMinutes(2);

    public MainViewModel()
    {
        Catalog? loaded = null;
        var error = "";

        try
        {
            loaded = Catalog.Load();
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        _catalog = loaded;
        _loadError = error;

        BuildGroups();

        IsAdministrator = PowerShell.IsAdministrator();
        UndoManifest = ManifestStore.LoadLatest();
        OSDescription = DescribeOs();

        // All three route unexpected failures through the same error sink the
        // methods use, so there is one place a user sees them.
        ScanCommand = new AsyncRelayCommand(_ => ScanAsync(), _ => !IsBusy, RelayUnexpected);
        PreviewCommand = new AsyncRelayCommand(_ => PreviewAsync(), _ => !IsBusy, RelayUnexpected);
        UndoCommand = new AsyncRelayCommand(_ => UndoAsync(), _ => !IsBusy && UndoManifest is not null, RelayUnexpected);

        PresetRecommendedCommand = new RelayCommand(_ => ApplyPreset(PresetKind.Recommended));
        PresetMinimalCommand = new RelayCommand(_ => ApplyPreset(PresetKind.Minimal));
        PresetThoroughCommand = new RelayCommand(_ => ApplyPreset(PresetKind.Thorough));
        PresetClearCommand = new RelayCommand(_ => ApplyPreset(PresetKind.Clear));

        SelectAllCommand = new RelayCommand(_ => SetAll(true));
        SelectNoneCommand = new RelayCommand(_ => SetAll(false));
        DropRiskyCommand = new RelayCommand(_ => DropRisky());
        SelectRiskyCommand = new RelayCommand(_ => SelectRisky());
        OpenLogsCommand = new RelayCommand(_ => OpenLogs());
        CopySummaryCommand = new RelayCommand(_ => CopySummary());
    }

    private enum PresetKind { Recommended, Minimal, Thorough, Clear }

    // ------------------------------------------------------------- commands ---

    public AsyncRelayCommand ScanCommand { get; }
    public AsyncRelayCommand PreviewCommand { get; }
    public AsyncRelayCommand UndoCommand { get; }
    public RelayCommand PresetRecommendedCommand { get; }
    public RelayCommand PresetMinimalCommand { get; }
    public RelayCommand PresetThoroughCommand { get; }
    public RelayCommand PresetClearCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand SelectNoneCommand { get; }
    public RelayCommand DropRiskyCommand { get; }
    public RelayCommand SelectRiskyCommand { get; }
    public RelayCommand OpenLogsCommand { get; }
    public RelayCommand CopySummaryCommand { get; }

    public event EventHandler<PlanReadyArgs>? PlanReady;
    public event EventHandler<RunFinishedArgs>? RunFinished;
    public event EventHandler<UndoFinishedArgs>? UndoFinished;

    public static Action<string> ErrorMessage { get; set; } =
        text => System.Windows.MessageBox.Show(text, "Windows Debloater",
            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);

    private void RelayError(string text) => ErrorMessage(text);

    /// <summary>Last-resort handler for failures that escaped a command body.</summary>
    private void RelayUnexpected(Exception ex)
    {
        AppendLog("Unexpected error: " + ex);
        RelayError("Something went wrong and the operation stopped.\n\n" + ex.Message);
    }

    // ------------------------------------------------------------- state ---

    public ObservableCollection<CategoryGroup> AppGroups { get; } = [];
    public ObservableCollection<CategoryGroup> TweakGroups { get; } = [];

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value)) RaiseCommands();
        }
    }

    private double _progressValue;
    public double ProgressValue
    {
        get => _progressValue;
        private set => Set(ref _progressValue, value);
    }

    private string _statusText = "Ready. Scan to check which bloat apps are installed.";
    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    private string _logText = "";
    public string LogText
    {
        get => _logText;
        private set => Set(ref _logText, value);
    }

    private bool _logExpanded;
    public bool LogExpanded
    {
        get => _logExpanded;
        set => Set(ref _logExpanded, value);
    }

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value)) ApplyFilter();
        }
    }

    private bool _allowRisky;
    public bool AllowRisky
    {
        get => _allowRisky;
        set
        {
            if (Set(ref _allowRisky, value))
            {
                // Revoking permission has to revoke the selections too. Leaving
                // them ticked would show checked boxes that silently do nothing.
                if (!value)
                {
                    foreach (var item in AllItems.Where(i => i.Risk >= RiskLevel.Moderate))
                    {
                        item.IsSelected = false;
                    }
                }

                RefreshEnablement();
                RefreshSummary();
            }
        }
    }

    public bool IsAdministrator { get; }
    public string OSDescription { get; }
    public RunManifest? UndoManifest { get; private set; }

    public string CatalogSource => _catalog?.Source ?? $"catalog unavailable: {_loadError}";

    public bool HasCatalog => _catalog is not null;

    public string UndoLabel => UndoManifest is null
        ? "Undo Last Run (nothing to undo)"
        : $"Undo Last Run ({UndoManifest.Records.Count} change(s), {UndoManifest.Timestamp})";

    public bool HasUndo => UndoManifest is not null;

    // ------------------------------------------------------------- setup ---

    private void BuildGroups()
    {
        if (_catalog is null) return;

        foreach (var group in _catalog.Apps.GroupBy(a => a.Category))
        {
            var categoryGroup = new CategoryGroup(group.Key);
            foreach (var app in group)
            {
                var item = new ItemViewModel(app);
                item.IsEnabled = !app.Protected;
                item.StatusText = app.Protected ? "protected" : "";
                categoryGroup.Items.Add(item);
            }
            categoryGroup.Attach();
            AppGroups.Add(categoryGroup);
        }

        foreach (var group in _catalog.Tweaks.GroupBy(t => t.Category))
        {
            var categoryGroup = new CategoryGroup(group.Key);
            foreach (var tweak in group)
            {
                categoryGroup.Items.Add(new ItemViewModel(tweak));
            }
            categoryGroup.Attach();
            TweakGroups.Add(categoryGroup);
        }
    }

    private static string DescribeOs()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine
                .OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var product = key?.GetValue("ProductName")?.ToString() ?? "Windows";
            var build = Environment.OSVersion.Version.Build;
            return $"{product} - build {build}";
        }
        catch
        {
            return Environment.OSVersion.VersionString;
        }
    }

    private IEnumerable<ItemViewModel> AllItems => AppGroups.SelectMany(g => g.Items).Concat(TweakGroups.SelectMany(g => g.Items));

    // ------------------------------------------------------------- scan ---

    private async Task ScanAsync()
    {
        if (_catalog is null)
        {
            RelayError($"Could not load the app catalog.\n\n{_loadError}");
            return;
        }

        IsBusy = true;
        StatusText = "Scanning installed packages, services and scheduled tasks...";
        AppendLog("Scanning this machine...");

        var started = Stopwatch.StartNew();

        try
        {
            _context = await Task.Run(() => PlanContext.Discover(_catalog.Apps, _catalog.Tweaks));
            _contextScannedAt = DateTimeOffset.Now;

            UpdateAvailability();
            RefreshEnablement();

            var selectable = AppGroups.SelectMany(g => g.Items).Count(i => i.IsEnabled && i.IsSelected);
            var wantedServices = TweakGroups
                .SelectMany(g => g.Items)
                .Count(i => i.Tweak!.Kind == ActionKind.Service);
            var wantedTasks = TweakGroups
                .SelectMany(g => g.Items)
                .Count(i => i.Tweak!.Kind == ActionKind.ScheduledTask);

            StatusText = $"Scan complete in {started.Elapsed.TotalSeconds:0.0}s. " +
                         $"{_context.Installed.Count} packages, {_context.Provisioned.Count} provisioned. " +
                         $"{selectable} change(s) currently selected.";

            AppendLog($"Scan complete: {_context.Installed.Count} installed packages, " +
                      $"{_context.Provisioned.Count} provisioned packages, " +
                      $"{_context.Services.Count} of {wantedServices} services present, " +
                      $"{_context.Tasks.Count} of {wantedTasks} scheduled tasks present.");
        }
        catch (Exception ex)
        {
            StatusText = "Scan failed.";
            AppendLog("Scan failed: " + ex.Message);
            RelayError("Could not read this system's package and service state.\n\n" + ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Locks app entries that are protected or not present, so the checkbox state
    /// reflects what is actually on the machine rather than what the catalog lists.
    /// </summary>
    private void UpdateAvailability()
    {
        if (_context is null) return;

        foreach (var item in AllItems)
        {
            if (item.App is not { } app) continue;

            if (app.Protected)
            {
                item.SetAvailability(present: false, isProtected: true);
                continue;
            }

            var present =
                _context.Installed.Any(p => app.IsMatching(p.Name)) ||
                _context.Provisioned.Keys.Any(p => app.IsMatching(p));

            item.SetAvailability(present, isProtected: false);
        }
    }

    // ---------------------------------------------------------- selection ---

    private bool IsEligible(ItemViewModel item) => item.IsEnabled && item.IsSelected;

    private IEnumerable<AppEntry> SelectedApps() =>
        AppGroups.SelectMany(g => g.Items).Where(IsEligible).Select(i => i.App!).Where(a => !a.Protected);

    private IEnumerable<Tweak> SelectedTweaks() =>
        TweakGroups.SelectMany(g => g.Items).Where(IsEligible).Select(i => i.Tweak!);

    private void RefreshEnablement()
    {
        foreach (var item in AppGroups.SelectMany(g => g.Items))
        {
            // Uses the recorded flags rather than comparing StatusText, which
            // would break silently if the wording ever changed.
            item.ApplyRiskPolicy(AllowRisky, locked: !item.IsPresent || item.IsProtected);
        }

        foreach (var item in TweakGroups.SelectMany(g => g.Items))
        {
            item.ApplyRiskPolicy(AllowRisky, locked: false);
        }

        foreach (var group in AppGroups.Concat(TweakGroups)) group.RefreshCounts();
        RefreshSummary();
    }

    private void ApplyPreset(PresetKind kind)
    {
        foreach (var group in AppGroups)
        {
            foreach (var item in group.Items)
            {
                var app = item.App!;
                var on = kind switch
                {
                    PresetKind.Clear => false,
                    PresetKind.Minimal => false,
                    PresetKind.Thorough => app.DefaultSelected && !app.Protected,
                    _ => app.DefaultSelected && !app.Protected
                };

                item.IsSelected = on && item.IsEnabled;
            }
        }

        foreach (var group in TweakGroups)
        {
            foreach (var item in group.Items)
            {
                var tweak = item.Tweak!;
                var on = kind switch
                {
                    PresetKind.Clear => false,
                    PresetKind.Minimal =>
                        tweak.Category == "Privacy & Telemetry" && tweak.DefaultOn,
                    PresetKind.Thorough => tweak.DefaultOn,
                    _ => tweak.DefaultOn && tweak.Risk <= RiskLevel.Low
                };

                item.IsSelected = on && item.IsEnabled;
            }
        }

        RefreshSummary();
        StatusText = kind switch
        {
            PresetKind.Recommended => "Recommended preset applied: low-risk bloat removal, privacy and interface tweaks.",
            PresetKind.Minimal => "Minimal preset applied: privacy tweaks only, nothing removed.",
            PresetKind.Thorough => "Thorough preset applied: everything flagged on by default, including moderate-risk items.",
            _ => "Selection cleared."
        };
    }

    private void SetAll(bool selected)
    {
        foreach (var item in AllItems)
        {
            if (item.IsEnabled) item.IsSelected = selected;
        }

        RefreshSummary();
    }

    private void DropRisky()
    {
        foreach (var item in AllItems)
        {
            if (item.Risk >= RiskLevel.Moderate) item.IsSelected = false;
        }

        RefreshSummary();
        StatusText = "Moderate and high-risk items deselected.";
    }

    private void SelectRisky()
    {
        foreach (var item in AllItems)
        {
            if (item.Risk >= RiskLevel.Moderate && item.IsEnabled) item.IsSelected = true;
        }

        RefreshSummary();
        StatusText = "All available moderate and high-risk items selected.";
    }

    private void RefreshSummary()
    {
        foreach (var group in AppGroups.Concat(TweakGroups)) group.RefreshCounts();
        Raise(nameof(UndoLabel));
    }

    private void ApplyFilter()
    {
        var filter = SearchText.Trim();

        foreach (var group in AppGroups.Concat(TweakGroups))
        {
            foreach (var item in group.Items) item.IsVisible = item.Matches(filter);
            group.IsVisible = group.Items.Any(i => i.IsVisible);
        }
    }

    // ---------------------------------------------------------- preview ---

    private async Task PreviewAsync()
    {
        if (IsBusy) return;

        if (_catalog is null)
        {
            RelayError($"Could not load the app catalog.\n\n{_loadError}");
            return;
        }

        planForApproval = null;
        IsBusy = true;

        try
        {
            // A cached scan can be arbitrarily old, and the preview is what the
            // user approves, so anything stale is re-read rather than trusted.
            if (_context is null || DateTimeOffset.Now - _contextScannedAt > ScanFreshness)
            {
                StatusText = "Refreshing system state before building the preview...";
                _context = await Task.Run(() => PlanContext.Discover(_catalog.Apps, _catalog.Tweaks));
                _contextScannedAt = DateTimeOffset.Now;
            }

            var apps = SelectedApps().ToList();
            var tweaks = SelectedTweaks().ToList();

            if (apps.Count == 0 && tweaks.Count == 0)
            {
                RelayError("Nothing is selected. Tick at least one item, or use a preset first.");
                return;
            }

            StatusText = "Building preview...";

            var plan = await Task.Run(() => PlanBuilder.Build(apps, tweaks, _context));

            if (plan.Count == 0)
            {
                RelayError("Every selected item is already in the requested state, so there is nothing to do.");
                return;
            }

            StatusText = $"{plan.Count(p => !p.AlreadyDone && !p.NotApplicable)} change(s) to apply, " +
                         $"{plan.Count(p => p.AlreadyDone)} already in the desired state, " +
                         $"{plan.Count(p => p.NotApplicable)} not present on this system.";

            var args = new PlanReadyArgs(plan, ApplyAsync);
            planForApproval = plan;

            // IsBusy is released before the dialog opens. The dialog is modal and
            // owns the interaction from here, so holding the flag would block the
            // apply it is waiting for.
            IsBusy = false;
            PlanReady?.Invoke(this, args);
        }
        catch (Exception ex)
        {
            planForApproval = null;
            IsBusy = false;
            StatusText = "Preview failed.";
            AppendLog("Preview failed: " + ex);
            RelayError("Could not build the preview.\n\n" + ex.Message);
        }
    }

    /// <summary>
    /// The exact plan the user was shown. Kept so a commit can be verified against
    /// the approval instead of trusting whatever the caller passes in.
    /// </summary>
    private IReadOnlyList<ActionItem>? planForApproval;

    /// <summary>
    /// True when the handed-over plan is still the one the user approved. Guards
    /// against a stale commit applying a different plan than the preview showed.
    /// </summary>
    public bool IsApprovedPlan(IReadOnlyList<ActionItem> plan) =>
        planForApproval is not null && planForApproval.SequenceEqual(plan);

    private async Task ApplyAsync(IReadOnlyList<ActionItem> plan)
    {
        if (!IsApprovedPlan(plan))
        {
            RelayError("This plan is no longer the one that was previewed. Preview again before applying.");
            return;
        }

        planForApproval = null;
        IsBusy = true;
        ProgressValue = 0;
        LogText = "";
        _log.Clear();

        var progress = new Progress<ApplyProgress>(p =>
        {
            ProgressValue = p.Fraction * 100;
            StatusText = p.Item is null
                ? p.Message
                : $"[{p.Index}/{p.Total}] {p.Item.Title}: {p.Message}";
            AppendLog(p.Failed ? $"FAIL  {p.Item?.Title}: {p.Message}" : $"  {p.Item?.Title}: {p.Message}");
        });

        RunResult? result = null;

        try
        {
            result = await Task.Run(() => Executor.Apply(plan, createRestorePoint: true, progress));
            UndoManifest = result.Manifest;
            RefreshSummary();
            Raise(nameof(HasUndo));
            Raise(nameof(UndoLabel));
        }
        catch (Exception ex)
        {
            AppendLog("Run aborted: " + ex);
            RelayError("The run was aborted before it finished.\n\n" + ex.Message);
        }
        finally
        {
            IsBusy = false;
            ProgressValue = 100;
        }

        if (result is not null) RunFinished?.Invoke(this, new RunFinishedArgs(result));
    }

    // ------------------------------------------------------------- undo ---

    private async Task UndoAsync()
    {
        var manifest = UndoManifest;
        if (manifest is null) return;

        IsBusy = true;
        ProgressValue = 0;

        var progress = new Progress<UndoStep>(p =>
        {
            ProgressValue = p.Fraction * 100;
            StatusText = $"[{p.Index}/{p.Total}] {p.Title}: {p.Message}";
            AppendLog($"  {p.Title}: {p.Message}");
        });

        UndoResult? result = null;

        try
        {
            result = await Task.Run(() => Executor.Undo(manifest, progress));

            if (result.Failed == 0)
            {
                UndoManifest = null;
                RefreshSummary();
                Raise(nameof(HasUndo));
                Raise(nameof(UndoLabel));
            }
        }
        catch (Exception ex)
        {
            RelayError("Undo failed.\n\n" + ex.Message);
        }
        finally
        {
            IsBusy = false;
            ProgressValue = 100;
        }

        if (result is not null) UndoFinished?.Invoke(this, new UndoFinishedArgs(result, manifest.Timestamp));
    }

    // --------------------------------------------------------------- misc ---

    private void OpenLogs()
    {
        try
        {
            ManifestStore.EnsureRoot();
            Process.Start(new ProcessStartInfo("explorer.exe", ManifestStore.Root) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            RelayError("Could not open the log folder.\n\n" + ex.Message);
        }
    }

    private void CopySummary()
    {
        var text = string.Join(Environment.NewLine,
            AllItems.Where(IsEligible).Select(i => $"{i.Category}\t{i.Title}"));

        PowerShell.CopyToClipboard(text);
        StatusText = $"{AllItems.Count(IsEligible)} selected item(s) copied to the clipboard.";
    }

    private void AppendLog(string line)
    {
        _log.AppendLine(line);
        if (_log.Length > 200_000) _log.Remove(0, _log.Length - 120_000);
        LogText = _log.ToString();
    }

    private void RaiseCommands()
    {
        ScanCommand.RaiseCanExecuteChanged();
        PreviewCommand.RaiseCanExecuteChanged();
        UndoCommand.RaiseCanExecuteChanged();
    }
}