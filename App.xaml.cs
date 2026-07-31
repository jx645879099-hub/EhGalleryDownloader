namespace EhGalleryDownloader;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstance;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            "Local\\EhGalleryDownloader.SingleInstance",
            out _ownsSingleInstance);
        if (!_ownsSingleInstance)
        {
            System.Windows.MessageBox.Show(
                "画廊下载助手已经在运行。请切换到现有窗口，不要重复启动。",
                "程序已运行",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);
    }

    public static void ApplyUiScale(int percent)
    {
        var normalized = percent switch { 100 => 100, 125 => 125, _ => 110 };
        var values = normalized switch
        {
            100 => new[] { 12d, 14d, 14d, 15d, 18d, 25d },
            125 => new[] { 16d, 17d, 17d, 18d, 22d, 31d },
            _ => new[] { 14d, 15d, 15d, 16d, 20d, 28d }
        };
        var resources = Current.Resources;
        resources["FontCaptionSize"] = values[0];
        resources["FontBodySize"] = values[1];
        resources["FontControlSize"] = values[2];
        resources["FontNavSize"] = values[3];
        resources["FontCardTitleSize"] = values[4];
        resources["FontPageTitleSize"] = values[5];
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        if (_ownsSingleInstance)
        {
            try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
        }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
