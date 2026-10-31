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
            e.Handled = true;
            Window?.ShowError(e.Exception.Message);
        };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        Settings = AppSettings.Load();
        Home = new HomeService(DispatcherQueue.GetForCurrentThread());
        Window = new MainWindow();
        Window.Activate();
        await Window.StartAsync();
    }
}
