using System.Windows;
using System.Windows.Threading;

namespace PuterScanny.App;

public partial class App : Application
{
    /// <summary>A scan to start right away (when PuterScanny restarted itself as administrator).</summary>
    public static Elevation.Request? StartupRequest { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandledException;
        StartupRequest = Elevation.Parse(e.Args);
        AppTheme.Load();
        base.OnStartup(e);
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"Something went wrong:\n\n{e.Exception.Message}\n\nThe app will keep running. If this repeats, please report it.",
            "PuterScanny", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
