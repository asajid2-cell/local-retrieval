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

        // Absolute path: a relative "Assets/AppIcon.ico" resolves against the working directory, which
        // is NOT the app folder when launched from a shortcut without a Start-in dir — then SetIcon fails
        // silently and the taskbar shows the generic icon. AppContext.BaseDirectory is always the app dir.
        var iconPath = System.IO.Path.Combine(System.AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (System.IO.File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1500, 980));
        Diag.Log("MW.ctor: appwindow configured");

        // Navigate the root frame to the main page on startup.
        RootFrame.Navigate(typeof(MainPage));
        Diag.Log("MW.ctor: navigated to MainPage");
    }
}
