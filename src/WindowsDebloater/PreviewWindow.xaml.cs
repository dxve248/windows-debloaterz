using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using WindowsDebloater.Core;

namespace WindowsDebloater;

public partial class PreviewWindow : Window
{
    private readonly IReadOnlyList<ActionItem> _items;

    public bool HasRisk => _items.Any(i => i.HasRisk);

    public PreviewWindow(IReadOnlyList<ActionItem> items)
    {
        InitializeComponent();

        _items = items;
        DataContext = this;

        ItemList.ItemsSource = items;
        ItemList.ItemTemplate = BuildRowTemplate();

        var willChange = items.Count(i => !i.AlreadyDone && !i.NotApplicable);
        var already = items.Count(i => i.AlreadyDone);
        var absent = items.Count(i => i.NotApplicable);
        var risky = items.Count(i => i.HasRisk && !i.OutcomeIsNoOp);

        SummaryLine.Text =
            $"{willChange} change(s) will be applied, {already} are already in the requested state, " +
            $"{absent} are not present on this system" +
            (risky > 0 ? $", and {risky} are flagged higher-risk." : ".");

        ApplyButton.IsEnabled = !HasRisk;
        Acknowledge.IsChecked = !HasRisk;
        Acknowledge.IsEnabled = HasRisk;

        Acknowledge.Checked += (_, _) => ApplyButton.IsEnabled = true;
        Acknowledge.Unchecked += (_, _) => ApplyButton.IsEnabled = !HasRisk;
    }

    /// <summary>Reads a brush from the app palette as a XAML colour string.</summary>
    private static string Color(string resourceKey) =>
        ((SolidColorBrush)Application.Current.Resources[resourceKey]).Color.ToString();

    /// <summary>
    /// The row template is parsed from XAML rather than assembled with
    /// FrameworkElementFactory: Grid.ColumnDefinitions and Style setters on a
    /// dependency property cannot be expressed through the factory API.
    ///
    /// A standalone XamlReader.Parse gets its own namescope, so StaticResource
    /// lookups against App.xaml would not resolve here. The palette values are
    /// therefore substituted into the markup, which keeps the template in step
    /// with App.xaml instead of drifting toward hardcoded hex literals.
    /// </summary>
    private static DataTemplate BuildRowTemplate()
    {
        var text = Color("TextBrush");
        var dim = Color("DimBrush");
        var faint = Color("FaintBrush");
        var accent = Color("AccentDeepBrush");
        var row = Color("CardBgBrush");

        var xaml = $$"""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                          xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
              <DataTemplate.Resources>
                <Style x:Key="OutcomeText" TargetType="TextBlock">
                  <Setter Property="Foreground" Value="{{accent}}"/>
                  <Style.Triggers>
                    <DataTrigger Binding="{Binding OutcomeIsNoOp}" Value="True">
                      <Setter Property="Foreground" Value="{{faint}}"/>
                    </DataTrigger>
                  </Style.Triggers>
                </Style>
              </DataTemplate.Resources>

              <Border Margin="0,0,0,2" Padding="8,5" CornerRadius="4"
                      Background="{{row}}">
                <Grid>
                  <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="140"/>
                    <ColumnDefinition Width="*"/>
                    <ColumnDefinition Width="230"/>
                    <ColumnDefinition Width="200"/>
                  </Grid.ColumnDefinitions>

                  <TextBlock Grid.Column="0" Text="{Binding Category}" FontSize="11.5"
                             Foreground="{{dim}}" Margin="0,1,8,1"
                             TextTrimming="CharacterEllipsis" VerticalAlignment="Top"/>

                  <StackPanel Grid.Column="1" Margin="0,1,10,1">
                    <TextBlock Text="{Binding Title}" FontSize="12.5" Foreground="{{text}}"/>
                    <TextBlock Text="{Binding Target}" FontSize="11" Foreground="{{faint}}"
                               Margin="0,2,0,0" TextTrimming="CharacterEllipsis"/>
                    <TextBlock Text="{Binding PlanCommand}" FontSize="10.5"
                               Foreground="{{accent}}"
                               FontFamily="Consolas, Cascadia Mono" Margin="0,2,0,0"
                               TextTrimming="CharacterEllipsis"/>
                  </StackPanel>

                  <TextBlock Grid.Column="2" Text="{Binding Current}" FontSize="11.5"
                             Foreground="{{dim}}" TextWrapping="Wrap" Margin="0,1,10,1"/>

                  <TextBlock Grid.Column="3" Text="{Binding OutcomeText}" FontSize="11.5"
                             Style="{StaticResource OutcomeText}" TextWrapping="Wrap"
                             Margin="0,1,0,1"/>
                </Grid>
              </Border>
            </DataTemplate>
            """;

        return (DataTemplate)XamlReader.Parse(xaml);
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        var builder = new StringBuilder();

        foreach (var item in _items)
        {
            builder.AppendLine($"{item.Category}\t{item.Title}");
            builder.AppendLine($"    target : {item.Target}");
            builder.AppendLine($"    current: {item.Current}");
            builder.AppendLine($"    outcome: {item.OutcomeText}");
            builder.AppendLine($"    command: {item.PlanCommand}");
            builder.AppendLine();
        }

        PowerShell.CopyToClipboard(builder.ToString());
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
