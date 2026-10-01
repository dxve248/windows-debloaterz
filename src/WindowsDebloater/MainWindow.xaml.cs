using System.Windows;
using WindowsDebloater.ViewModels;

namespace WindowsDebloater;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        _viewModel = new MainViewModel();
        DataContext = _viewModel;

        _viewModel.PlanReady += OnPlanReady;
        _viewModel.RunFinished += OnRunFinished;
        _viewModel.UndoFinished += OnUndoFinished;

        // Scanned from Loaded rather than the constructor so the dispatcher
        // synchronization context is installed before any binding updates.
        Loaded += (_, _) => _viewModel.ScanCommand.Execute(null);
    }

    private async void OnPlanReady(object? sender, PlanReadyArgs e)
    {
        var dialog = new PreviewWindow(e.Items);

        if (dialog.ShowDialog() != true) return;

        // async void is required by the event signature, so the exception is
        // caught inside ApplyAsync. The run reports its own failures and the
        // RunFinished dialog, so there is nothing to rethrow.
        await e.Commit(e.Items);
    }

    private void OnRunFinished(object? sender, RunFinishedArgs e)
    {
        var result = e.Result;

        var headline = result.Failed == 0
            ? $"Applied {result.Succeeded} change(s)."
            : $"Applied {result.Succeeded}, but {result.Failed} failed.";

        var lines = new List<string>
        {
            $"Applied:          {result.Succeeded}",
            $"Already correct:  {result.Skipped}",
            $"Failed:           {result.Failed}",
            $"Restore point:    {(result.RestorePointCreated ? result.Manifest.RestorePointDescription : "not created")}",
            "",
            $"Undo manifest:    {result.ManifestPath}",
            $"Activity log:     {result.LogPath}"
        };

        if (!string.IsNullOrWhiteSpace(result.RestorePointWarning))
        {
            lines.Add("");
            lines.Add("WARNING: " + result.RestorePointWarning);
        }

        if (result.Unrestorable.Count > 0)
        {
            lines.Add("");
            lines.Add("Removed apps cannot be reinstated automatically. Reinstall from the Microsoft Store:");
            lines.AddRange(result.Unrestorable.Select(u => $"  - {u.Title}  [{u.Target}]"));
        }

        if (result.Errors.Count > 0)
        {
            lines.Add("");
            lines.Add("Failures:");
            lines.AddRange(result.Errors.Select(x => $"  - {x}"));
        }

        lines.Add("");
        lines.Add(result.RequiresReboot
            ? "A restart is required for some of these changes to take full effect."
            : "All of this takes effect immediately. Sign out or restart if something does not look right.");

        var dialog = new ResultWindow(
            "Run complete", headline, lines, offerUndo: result.Succeeded > 0);

        if (dialog.ShowDialog() == true && dialog.UndoRequested)
        {
            _viewModel.UndoCommand.Execute(null);
        }
    }

    private void OnUndoFinished(object? sender, UndoFinishedArgs e)
    {
        var result = e.Result;

        var headline = result.Failed == 0
            ? $"Reverted {result.Restored} change(s) from run {e.Source}."
            : $"Reverted {result.Restored}, but {result.Failed} failed.";

        var lines = new List<string>
        {
            $"Restored:  {result.Restored}",
            $"Failed:    {result.Failed}",
            $"Total:     {result.Total}"
        };

        if (result.Manual.Count > 0)
        {
            lines.Add("");
            lines.Add("These have to be reinstalled from the Microsoft Store:");
            lines.AddRange(result.Manual.Select(x => $"  - {x}"));
        }

        if (result.Errors.Count > 0)
        {
            lines.Add("");
            lines.Add("Failures:");
            lines.AddRange(result.Errors.Select(x => $"  - {x}"));
        }

        new ResultWindow("Undo complete", headline, lines, offerUndo: false).ShowDialog();
    }
}