using Microsoft.UI.Xaml;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace CodexLocalRetrieval_Native;

/// <summary>
/// The application window. This hosts a Frame that displays pages. Add your
/// UI and logic to MainPage.xaml / MainPage.xaml.cs instead of here so you
/// can use Page features such as navigation events and the Loaded lifecycle.
/// </summary>
public sealed partial class MainWindow : Window
{
    // Static handle so the capture harness can resize the window for viewport tests.
    public static MainWindow? Instance { get; private set; }

    public MainWindow()
    {
        Instance = this;
        Diag.Log("MW.ctor: before InitializeComponent");
        InitializeComponent();
        Diag.Log("MW.ctor: after InitializeComponent");

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        Diag.Log("MW.ctor: titlebar set");

        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1500, 980));
        Diag.Log("MW.ctor: appwindow configured");

        // Navigate the root frame to the main page on startup.
        RootFrame.Navigate(typeof(MainPage));
        Diag.Log("MW.ctor: navigated to MainPage");
    }
}
