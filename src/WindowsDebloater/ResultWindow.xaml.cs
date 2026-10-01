using System.Windows;

namespace WindowsDebloater;

public partial class ResultWindow : Window
{
    /// <summary>Set when the user asks for the run to be reverted from this dialog.</summary>
    public bool UndoRequested { get; private set; }

    public ResultWindow(string title, string headline, IEnumerable<string> details, bool offerUndo)
    {
        InitializeComponent();

        Title = title;
        HeadlineText.Text = headline;
        DetailsBox.Text = string.Join(Environment.NewLine, details);
        UndoButton.Visibility = offerUndo ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnUndo(object sender, RoutedEventArgs e)
    {
        UndoRequested = true;
        DialogResult = true;
        Close();
    }
}