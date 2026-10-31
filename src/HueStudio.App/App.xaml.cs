using HueStudio.App.Services;
using HueStudio.Core.Storage;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace HueStudio.App;

public partial class App : Application
{
    public static HomeService Home { get; private set; } = null!;
    public static AppSettings Settings { get; private set; } = null!;
    public static CredentialStore Credentials { get; } = new(AppPaths.Bridges, new DpapiProtector());
    public static MainWindow Window { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            LogCrash(e.Exception);
            if (Window is null) return; // no window to show it in: let the app exit rather than hang invisibly
            e.Handled = true;
            Window.ShowError(e.Exception.Message);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { LogCrash(e.Exception); e.SetObserved(); };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            Settings = AppSettings.Load();
            Home = new HomeService(DispatcherQueue.GetForCurrentThread());
            Window = new MainWindow();
            Window.Activate();
            await Window.StartAsync();
        }
        catch (Exception ex)
        {
            LogCrash(ex);
            if (Window is null) Exit();
            else Window.ShowError(ex.Message);
        }
    }

    /// <summary>Appends to %LOCALAPPDATA%\HueStudio\crash.log so start-up failures are never silent.</summary>
    public static void LogCrash(Exception? ex)
    {
        if (ex is null) return;
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            File.AppendAllText(Path.Combine(AppPaths.Root, "crash.log"), $"[{DateTimeOffset.Now:u}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { /* logging must never throw */ }
    }
}
