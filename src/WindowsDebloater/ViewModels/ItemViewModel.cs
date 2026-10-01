using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using WindowsDebloater.Core;

namespace WindowsDebloater.ViewModels;

public class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => execute(parameter);

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// Fire-and-forget async command. ICommand.Execute is void, so an unobserved
/// exception would otherwise tear down the dispatcher; <paramref name="onError"/>
/// is the single place such failures are surfaced.
/// </summary>
public sealed class AsyncRelayCommand(
    Func<object?, Task> execute,
    Func<object?, bool>? canExecute = null,
    Action<Exception>? onError = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;

        // Called straight from the UI thread so the async body starts here and
        // any Progress<T> it creates captures the dispatcher context.
        _ = RunAsync();

        async Task RunAsync()
        {
            try
            {
                await execute(parameter);
            }
            catch (OperationCanceledException)
            {
                // Expected when the user cancels a run.
            }
            catch (Exception ex)
            {
                (onError ?? DefaultError)(ex);
            }
        }
    }

    private static void DefaultError(Exception ex) =>
        System.Windows.MessageBox.Show(
            $"Unexpected error: {ex.Message}", "Windows Debloater",
            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class ItemViewModel : ObservableObject
{
    public Tweak? Tweak { get; }
    public AppEntry? App { get; }
    public bool IsApp => App is not null;

    public string Title => IsApp ? App!.Name : Tweak!.Title;
    public string Description => IsApp ? App!.Description : Tweak!.Description;
    public string Category => IsApp ? App!.Category : Tweak!.Category;
    public RiskLevel Risk => IsApp ? App!.Risk : Tweak!.Risk;

    public string RiskBadge => Risk switch
    {
        RiskLevel.Moderate => "moderate risk",
        RiskLevel.High => "high risk",
        _ => ""
    };

    public bool HasRiskBadge => RiskBadge.Length > 0;

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }

    private bool _isEnabled = true;
    public bool IsEnabled
    {
        get => _isEnabled;
        set => Set(ref _isEnabled, value);
    }

    private bool _isVisible = true;
    public bool IsVisible
    {
        get => _isVisible;
        set => Set(ref _isVisible, value);
    }

    private string _statusText = "";
    public string StatusText
    {
        get => _statusText;
        set
        {
            if (Set(ref _statusText, value)) Raise(nameof(HasStatus));
        }
    }

    public bool HasStatus => !string.IsNullOrWhiteSpace(_statusText);

    public ItemViewModel(Tweak tweak)
    {
        Tweak = tweak;
        _isSelected = tweak.DefaultOn;
    }

    public ItemViewModel(AppEntry app)
    {
        App = app;
        _isSelected = app.DefaultSelected && !app.Protected;
    }

    /// <summary>
    /// Records whether the target exists on this machine. A protected entry is
    /// reported as protected rather than absent, because "not installed" would
    /// imply the blocklist is conditional when it is not.
    /// </summary>
    public void SetAvailability(bool present, bool isProtected)
    {
        IsPresent = present;
        IsProtected = isProtected;
        IsEnabled = !isProtected && present;
        StatusText = isProtected ? "protected" : present ? "" : "not installed";
    }

    public bool IsPresent { get; private set; } = true;
    public bool IsProtected { get; private set; }

    /// <summary>Re-evaluates whether the checkbox is clickable given the risk policy.</summary>
    public void ApplyRiskPolicy(bool allowRisky, bool locked)
    {
        IsEnabled = !locked && (allowRisky || Risk <= RiskLevel.Low);
    }

    public bool Matches(string filter) =>
        filter.Length == 0 ||
        Title.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        Description.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
        Category.Contains(filter, StringComparison.OrdinalIgnoreCase);
}

public sealed class CategoryGroup : ObservableObject
{
    public string Name { get; }
    public ObservableCollection<ItemViewModel> Items { get; } = [];

    public CategoryGroup(string name) => Name = name;

    private bool _isExpanded = true;
    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }

    private bool _isVisible = true;
    public bool IsVisible
    {
        get => _isVisible;
        set => Set(ref _isVisible, value);
    }

    public string CountText => $"{Items.Count(i => i.IsSelected)} of {Items.Count} selected";

    public void RefreshCounts() => Raise(nameof(CountText));

    public void Attach()
    {
        foreach (var item in Items)
        {
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ItemViewModel.IsSelected) ||
                    e.PropertyName == nameof(ItemViewModel.IsVisible))
                {
                    RefreshCounts();
                }
            };
        }
    }
}