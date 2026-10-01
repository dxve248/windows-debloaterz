using System.Windows;

namespace WindowsDebloater;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // MainWindow builds and owns its own view model; creating a second one
        // here would load the catalog twice and leave two independent scans.
        var window = new WindowsDebloater.MainWindow();

        MainWindow = window;
        window.Show();
    }
}