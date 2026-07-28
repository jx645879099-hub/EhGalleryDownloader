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
