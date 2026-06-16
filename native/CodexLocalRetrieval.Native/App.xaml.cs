using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace CodexLocalRetrieval_Native;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Window? _window;
    
    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        Diag.Log("App.ctor: before InitializeComponent");
        InitializeComponent();
        Diag.Log("App.ctor: after InitializeComponent");
        UnhandledException += (s, e) => Diag.Log("App.UnhandledException: " + e.Message + " | " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (s, e) => Diag.Log("AppDomain.Unhandled: " + e.ExceptionObject);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) => { Diag.Log("UnobservedTask: " + e.Exception); e.SetObserved(); };
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        Diag.Log("OnLaunched: start");
        _window = new MainWindow();
        Diag.Log("OnLaunched: MainWindow created");
        _window.Activate();
        Diag.Log("OnLaunched: activated");
    }
}
