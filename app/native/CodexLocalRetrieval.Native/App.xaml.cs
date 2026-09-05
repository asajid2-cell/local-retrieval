using System.Diagnostics;
using System.Runtime.InteropServices;
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

    // Held for the whole process lifetime so a second launch can detect us. Static so the GC
    // never collects it (which would silently release the lock).
    private static System.Threading.Mutex? _instanceMutex;
    private const string SingleInstanceName = @"Local\MUX.SingleInstance";

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    private const int SW_RESTORE = 9;

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

        // Single-instance: this app is a hub you reopen constantly. Without this guard, every
        // shortcut click spawns ANOTHER ~450MB process; the copies then thrash the CPU and fight
        // over app-store.json (IOException on save), which is what made everything feel slow.
        // A second launch just brings the live window forward and exits the duplicate.
        _instanceMutex = new System.Threading.Mutex(initiallyOwned: true, SingleInstanceName, out var createdNew);
        if (!createdNew)
        {
            Diag.Log("OnLaunched: another instance is live -> focusing it, exiting this duplicate");
            ActivateExistingInstance();
            Environment.Exit(0);
            return;
        }

        _window = new MainWindow();
        Diag.Log("OnLaunched: MainWindow created");
        _window.Activate();
        Diag.Log("OnLaunched: activated");
    }

    // Bring the already-running instance's window to the foreground so the user sees the app they
    // expected instead of nothing happening.
    private static void ActivateExistingInstance()
    {
        try
        {
            var me = Process.GetCurrentProcess();
            foreach (var p in Process.GetProcessesByName(me.ProcessName))
            {
                if (p.Id == me.Id) continue;
                var h = p.MainWindowHandle;
                if (h != IntPtr.Zero)
                {
                    ShowWindow(h, SW_RESTORE);
                    SetForegroundWindow(h);
                    break;
                }
            }
        }
        catch (Exception ex) { Diag.Log("ActivateExistingInstance failed: " + ex); }
    }
}
