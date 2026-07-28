using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace EhGalleryDownloader;

public partial class MainWindow : Window
{
    private static readonly string[] GroupTypes =
        ["Selector", "URLTest", "Fallback", "LoadBalance", "Relay"];
    private static readonly string[] SpecialNodeTerms =
        ["剩余流量", "套餐到期", "距离下次", "官网", "公告", "自动选择", "故障转移"];

    public ObservableCollection<DownloadJob> Jobs { get; } = [];
    public ObservableCollection<NodeProbeResult> NodeResults { get; } = [];

    private readonly EngineManager _engineManager = new();
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _cookieSaveTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(700)
    };
    private readonly string _nodeRecoveryPath =
        Path.Combine(SettingsStore.DataDirectory, "node-test-recovery.json");
    private GalleryDlService? _service;
    private CancellationTokenSource? _downloadCancellation;
    private CancellationTokenSource? _cookieVerificationCancellation;
    private CancellationTokenSource? _nodeTestCancellation;
    private CancellationTokenSource? _engineUpdateCancellation;
    private DownloadJob? _runningJob;
    private MihomoClient? _mihomoClient;
    private ClashState? _clashState;
    private IReadOnlyList<Uri>? _gallerySamples;
    private string? _nodeTestGroup;
    private string? _nodeBeforeTest;
    private string? _resultGroup;
    private bool _isNodeTesting;
    private bool _syncingCookieInputs;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        _cookieSaveTimer.Tick += (_, _) =>
        {
            _cookieSaveTimer.Stop();
            PersistManualCookie(showStatus: true);
        };
        _settings = SettingsStore.Load();
        SettingsStore.CleanupStaleRunFiles();
        ApplySettings();
        Loaded += async (_, _) =>
        {
            if (AutoDetectProxyCheckBox.IsChecked == true)
                await RefreshAutoDetectedProxyAsync(showStatus: false);
            await RefreshEngineStatusAsync();
            if (File.Exists(_nodeRecoveryPath))
                await EnsureNodeTesterConnectedAsync(showError: false);
        };
        Closing += (_, _) =>
        {
            _downloadCancellation?.Cancel();
            _cookieVerificationCancellation?.Cancel();
            _nodeTestCancellation?.Cancel();
            _engineUpdateCancellation?.Cancel();
            _service?.Dispose();
            _mihomoClient?.Dispose();
            SaveSettings();
        };
    }

    private void ApplySettings()
    {
        OutputPathTextBox.Text = _settings.OutputDirectory;
        ProxyTextBox.Text = _settings.ProxyUrl;
        UseProxyCheckBox.IsChecked = _settings.UseProxy;
        AutoDetectProxyCheckBox.IsChecked = _settings.AutoDetectProxy;
        ProxyDetectionStatusText.Text = _settings.AutoDetectProxy
            ? "等待自动识别"
            : "使用手动地址";
        ForceIpv4CheckBox.IsChecked = _settings.ForceIpv4;
        MetadataCheckBox.IsChecked = _settings.WriteMetadata;
        CbzCheckBox.IsChecked = _settings.PackageAsCbz;
        SelectComboByTag(LoginModeComboBox,
            string.IsNullOrWhiteSpace(_settings.LoginMode)
                ? (_settings.UseBrowserCookies ? _settings.Browser : "manual")
                : _settings.LoginMode);
        SelectComboByTag(QualityComboBox, _settings.DownloadOriginal ? "original" : "resized");
        var autoSaveCookie =
            !_settings.CookieStoragePreferenceSet || _settings.RememberCookie;
        RememberCookieCheckBox.IsChecked = autoSaveCookie;
        if (autoSaveCookie)
        {
            var savedCookie = SecretStore.LoadCookie();
            if (!string.IsNullOrWhiteSpace(savedCookie))
            {
                _syncingCookieInputs = true;
                CookiePasswordBox.Password = savedCookie;
                _syncingCookieInputs = false;
                CookieStatusText.Text = "已载入本机加密保存的 Cookie，建议点击“验证登录”。";
            }
        }
        UpdateLoginModeUi();
    }

    private void SaveSettings()
    {
        try
        {
            var loginMode = GetSelectedTag(LoginModeComboBox, "manual");
            _settings.OutputDirectory = OutputPathTextBox.Text.Trim();
            _settings.LoginMode = loginMode;
            if (loginMode is "edge" or "chrome" or "firefox")
                _settings.Browser = loginMode;
            _settings.UseBrowserCookies = loginMode is "edge" or "chrome" or "firefox";
            _settings.RememberCookie = RememberCookieCheckBox.IsChecked == true;
            _settings.CookieStoragePreferenceSet = true;
            _settings.UseProxy = UseProxyCheckBox.IsChecked == true;
            _settings.AutoDetectProxy = AutoDetectProxyCheckBox.IsChecked == true;
            _settings.ProxyUrl = NormalizeProxy(ProxyTextBox.Text);
            _settings.DownloadOriginal = GetSelectedTag(QualityComboBox, "original") == "original";
            _settings.PackageAsCbz = CbzCheckBox.IsChecked == true;
            _settings.WriteMetadata = MetadataCheckBox.IsChecked == true;
            _settings.ForceIpv4 = ForceIpv4CheckBox.IsChecked == true;
            if (loginMode == "manual")
                CookiePersistence.SaveIfEnabled(
                    GetManualCookie(), _settings.RememberCookie);
            SettingsStore.Save(_settings);
        }
        catch { }
    }

    private async Task RefreshEngineStatusAsync()
    {
        try
        {
            var version = await _engineManager.GetVersionAsync();
            if (string.IsNullOrWhiteSpace(version))
            {
                EngineDot.Fill = (Brush)FindResource("DangerBrush");
                EngineStatusText.Text = "缺少下载内核";
                InstallEngineButton.Content = "安装内核";
                FooterStatusText.Text = "请点击右上角“安装内核”，软件会通过 Clash 下载 gallery-dl。";
            }
            else
            {
                EngineDot.Fill = (Brush)FindResource("SuccessBrush");
                EngineStatusText.Text = $"内核 {version}";
                InstallEngineButton.Content = "检查更新";
                FooterStatusText.Text = "准备就绪。推荐粘贴网站 Cookie；Chrome 可以保持开启。";
            }
        }
        catch (Exception ex)
        {
            EngineDot.Fill = (Brush)FindResource("DangerBrush");
            EngineStatusText.Text = "内核检查失败";
            FooterStatusText.Text = ex.Message;
        }
    }

    private async void InstallEngineButton_Click(object sender, RoutedEventArgs e)
    {
        if (_runningJob is not null || _isNodeTesting) return;

        _engineUpdateCancellation?.Cancel();
        _engineUpdateCancellation?.Dispose();
        _engineUpdateCancellation = new CancellationTokenSource();
        InstallEngineButton.IsEnabled = false;
        EngineStatusText.Text = "正在检查…";
        EngineDot.Fill = (Brush)FindResource("WarningBrush");
        ActivityProgressBar.IsIndeterminate = true;
        ActivityProgressBar.Value = 0;

        try
        {
            await RefreshAutoDetectedProxyAsync(
                showStatus: true,
                _engineUpdateCancellation.Token);
            var proxy = UseProxyCheckBox.IsChecked == true ? NormalizeProxy(ProxyTextBox.Text) : null;
            var update = await _engineManager.CheckForUpdateAsync(
                proxy, _engineUpdateCancellation.Token);
            if (update.IsLatest)
            {
                FooterStatusText.Text = $"当前已是最新版（{update.LocalVersion}），没有下载任何文件。";
                MessageBox.Show($"当前内核 {update.LocalVersion} 已经是最新版，不需要下载。",
                    "无需更新", MessageBoxButton.OK, MessageBoxImage.Information);
                await RefreshEngineStatusAsync();
                return;
            }

            if (!update.EngineMissing)
            {
                var answer = MessageBox.Show(
                    $"发现新的内核版本。\n\n当前版本：{update.LocalVersion ?? "未知"}\n最新构建：{update.RemoteVersion}\n\n是否下载并更新？",
                    "发现更新", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes)
                {
                    FooterStatusText.Text = "已取消更新，继续使用当前内核。";
                    await RefreshEngineStatusAsync();
                    return;
                }
            }

            EngineStatusText.Text = update.EngineMissing ? "正在安装…" : "正在更新…";
            ActivityProgressBar.IsIndeterminate = false;
            var progress = new Progress<double>(value => ActivityProgressBar.Value = value);
            await _engineManager.InstallAsync(
                update, proxy, progress, _engineUpdateCancellation.Token);
            FooterStatusText.Text = update.EngineMissing ? "下载内核安装完成。" : "下载内核更新完成。";
            await RefreshEngineStatusAsync();
        }
        catch (OperationCanceledException)
        {
            FooterStatusText.Text = "内核更新已取消，原来的内核没有被修改。";
            await RefreshEngineStatusAsync();
        }
        catch (Exception ex)
        {
            var existingVersion = await _engineManager.GetVersionAsync();
            var explanation = DescribeEngineUpdateFailure(ex, existingVersion);
            EngineStatusText.Text = string.IsNullOrWhiteSpace(existingVersion)
                ? "缺少下载内核"
                : $"内核 {existingVersion}";
            EngineDot.Fill = (Brush)FindResource(
                string.IsNullOrWhiteSpace(existingVersion) ? "DangerBrush" : "SuccessBrush");
            FooterStatusText.Text = explanation;
            MessageBox.Show(
                explanation,
                string.IsNullOrWhiteSpace(existingVersion)
                    ? "安装内核失败"
                    : "更新未完成（当前内核仍可使用）",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _engineUpdateCancellation?.Dispose();
            _engineUpdateCancellation = null;
            InstallEngineButton.IsEnabled = true;
            ActivityProgressBar.IsIndeterminate = false;
            ActivityProgressBar.Value = 0;
        }
    }

    private static string DescribeEngineUpdateFailure(Exception exception, string? existingVersion)
    {
        var suffix = string.IsNullOrWhiteSpace(existingVersion)
            ? ""
            : $"\n\n当前内核 {existingVersion} 没有被修改，可以继续下载。";
        if (exception is EngineUpdateException updateError)
        {
            var message = updateError.Kind switch
            {
                EngineUpdateFailureKind.RateLimited =>
                    "官方发布服务器暂时限制了更新请求。这不代表 Clash 没有运行，请稍后再试。",
                EngineUpdateFailureKind.ProxyUnavailable =>
                    "无法连接软件中填写的本地代理地址。请核对“使用 Clash 代理”旁边的地址；能正常浏览网页并不一定代表该 HTTP 端口相同。",
                EngineUpdateFailureKind.TimedOut =>
                    "访问官方发布页超时。当前节点到 GitHub 的线路可能较慢，可以换节点后再检查。",
                EngineUpdateFailureKind.ReleaseUnavailable =>
                    "官方正在发布新版本，但 Windows 文件暂时还没准备好，请稍后再试。",
                EngineUpdateFailureKind.InvalidDownload =>
                    updateError.Message,
                _ => updateError.Message
            };
            return message + suffix;
        }

        return $"更新过程中发生错误：{exception.GetBaseException().Message}" + suffix;
    }

    private void PasteButton_Click(object sender, RoutedEventArgs e)
    {
        if (Clipboard.ContainsText())
            UrlTextBox.Text = Clipboard.GetText().Trim();
        UrlTextBox.Focus();
        UrlTextBox.CaretIndex = UrlTextBox.Text.Length;
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择画廊保存位置",
            Multiselect = false
        };
        if (Directory.Exists(OutputPathTextBox.Text))
            dialog.InitialDirectory = OutputPathTextBox.Text;
        if (dialog.ShowDialog(this) == true)
            OutputPathTextBox.Text = dialog.FolderName;
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_runningJob is not null) return;
        await RefreshAutoDetectedProxyAsync(showStatus: false);
        var url = UrlTextBox.Text.Trim();
        if (!TryValidateInputs(url, out var message))
        {
            MessageBox.Show(message, "还差一点", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!ConfirmBrowserCookieAccess())
            return;

        var job = DownloadTaskLogic.FindReusable(Jobs, url) ?? CreateJob(url);
        if (!Jobs.Contains(job))
            Jobs.Insert(0, job);
        else
            RefreshRetryOptions(job);
        JobsGrid.SelectedItem = job;
        await RunJobAsync(job);
    }

    private async void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_runningJob is not null || JobsGrid.SelectedItem is not DownloadJob selected) return;
        await RefreshAutoDetectedProxyAsync(showStatus: false);
        if (!TryValidateInputs(selected.Url, out var message, selected.OutputDirectory))
        {
            MessageBox.Show(message, "无法继续", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!ConfirmBrowserCookieAccess())
            return;
        RefreshRetryOptions(selected);
        await RunJobAsync(selected);
    }

    private DownloadJob CreateJob(string url)
    {
        var output = OutputPathTextBox.Text.Trim();
        return new DownloadJob
        {
            Url = url,
            OutputDirectory = output,
            Options = CreateCurrentOptions(
                GetSelectedTag(QualityComboBox, "original") == "original",
                CbzCheckBox.IsChecked == true,
                MetadataCheckBox.IsChecked == true)
        };
    }

    private void RefreshRetryOptions(DownloadJob job) =>
        job.Options = CreateCurrentOptions(
            job.Options.DownloadOriginal,
            job.Options.PackageAsCbz,
            job.Options.WriteMetadata);

    private DownloadOptions CreateCurrentOptions(
        bool downloadOriginal,
        bool packageAsCbz,
        bool writeMetadata)
    {
        var loginMode = GetSelectedTag(LoginModeComboBox, "manual");
        var useBrowserCookies = loginMode is "edge" or "chrome" or "firefox";
        return new DownloadOptions(
            loginMode,
            useBrowserCookies ? loginMode : "edge",
            useBrowserCookies,
            loginMode == "manual" ? GetManualCookie() : null,
            UseProxyCheckBox.IsChecked == true,
            NormalizeProxy(ProxyTextBox.Text),
            downloadOriginal,
            packageAsCbz,
            writeMetadata,
            ForceIpv4CheckBox.IsChecked == true);
    }

    private async Task RunJobAsync(DownloadJob job)
    {
        var engine = _engineManager.FindEngine();
        if (engine is null)
        {
            MessageBox.Show("还没有安装 gallery-dl 下载内核。请先点右上角“安装 / 更新”。",
                "缺少内核", MessageBoxButton.OK, MessageBoxImage.Information);
            job.State = "失败";
            job.Details = "缺少 gallery-dl 下载内核。";
            return;
        }
        if (!string.IsNullOrWhiteSpace(job.BlockedEngineVersion))
        {
            var currentVersion = await _engineManager.GetVersionAsync();
            if (string.Equals(
                    currentVersion,
                    job.BlockedEngineVersion,
                    StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show(
                    $"这个任务上次已确认是下载内核兼容错误（KeyError i3）。\n\n"
                    + $"当前仍是同一个内核 {currentVersion}，继续点击只会立即失败。"
                    + "\n请先点击右上角“检查更新”，更新成功后再继续。",
                    "需要先更新下载内核",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }
            job.BlockedEngineVersion = null;
        }

        SaveSettings();
        job.BeginAttempt();
        _runningJob = job;
        _downloadCancellation = new CancellationTokenSource();
        _service = new GalleryDlService();
        job.State = "下载中";
        job.Details = job.AttemptCount > 1
            ? $"正在继续第 {job.AttemptCount} 次尝试；已有完整文件会直接跳过。"
            : "正在读取画廊信息，请稍候。首次读取登录信息可能需要几秒钟。";
        SetRunningUi(true);
        CountText.Text = "";

        _service.CurrentFileChanged += file => RunOnUi(() =>
        {
            job.CurrentFile = file;
            job.Details = $"正在处理：{file}";
            RefreshSelectedDetails();
        });
        _service.FileCompleted += () => RunOnUi(() =>
        {
            job.CompletedFiles++;
            RefreshCounts(job);
        });
        _service.FileSkipped += () => RunOnUi(() =>
        {
            job.SkippedFiles++;
            RefreshCounts(job);
        });
        _service.FileFailed += () => RunOnUi(() =>
        {
            job.FailedFiles++;
            RefreshCounts(job);
        });
        _service.OutputReceived += line => RunOnUi(() =>
        {
            job.Details = line;
            RefreshSelectedDetails();
        });

        try
        {
            var exitCode = await _service.RunAsync(engine, job, _downloadCancellation.Token);
            job.FinishedAt = DateTime.Now;
            if (_downloadCancellation.IsCancellationRequested || exitCode == -1)
            {
                job.State = "已停止";
                job.Details = "任务已停止。已完成的图片会保留；下次重新尝试时会跳过成品，并续传 .part 临时文件。";
                FooterStatusText.Text = "任务已停止，可随时选中后重新尝试。";
            }
            else if (exitCode == 0)
            {
                job.State = "已完成";
                job.Details = job.SkippedFiles > 0
                    ? $"下载完成。本次新完成 {job.CompletedFiles} 个文件，已有并跳过 {job.SkippedFiles} 个文件。"
                    : $"下载完成，共完成 {job.CompletedFiles} 个文件。";
                FooterStatusText.Text = "下载完成。";
            }
            else
            {
                if (_service.LastRawError.Contains(
                        "KeyError - 'i3'", StringComparison.OrdinalIgnoreCase))
                {
                    job.State = "需要更新内核";
                    job.BlockedEngineVersion = await _engineManager.GetVersionAsync();
                    job.Details =
                        "下载内核兼容错误（KeyError i3）。本次任务没有继续运行；"
                        + "请先更新内核，再点击“继续 / 补漏选中任务”。重复使用当前内核不会改善。";
                    FooterStatusText.Text = "任务需要先更新下载内核，已阻止无意义的重复尝试。";
                }
                else
                {
                    job.State = "失败";
                    if (!job.Details.Contains("失败") && !job.Details.Contains("错误"))
                        job.Details =
                            $"下载没有完整完成（内核返回代码 {exitCode}）。"
                            + "可复制说明，或换节点后点“继续 / 补漏选中任务”。";
                    FooterStatusText.Text = "任务没有完整完成，可以换节点后重新尝试。";
                }
            }
        }
        catch (Exception ex)
        {
            job.FinishedAt = DateTime.Now;
            job.State = "失败";
            job.Details = FriendlyError(ex.Message);
            FooterStatusText.Text = job.Details;
        }
        finally
        {
            _service.Dispose();
            _service = null;
            _downloadCancellation.Dispose();
            _downloadCancellation = null;
            _runningJob = null;
            SetRunningUi(false);
            RefreshCounts(job);
            RefreshSelectedDetails();
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled = false;
        FooterStatusText.Text = "正在安全停止；已经下完的文件不会删除。";
        _downloadCancellation?.Cancel();
        _service?.Stop();
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var path = JobsGrid.SelectedItem is DownloadJob selected
            ? selected.OutputDirectory
            : OutputPathTextBox.Text.Trim();
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法打开保存位置：{ex.Message}", "打开失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void JobsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RetryButton.IsEnabled = _runningJob is null && JobsGrid.SelectedItem is DownloadJob;
        RefreshSelectedDetails();
    }

    private void RefreshSelectedDetails()
    {
        if (JobsGrid.SelectedItem is DownloadJob selected)
            DetailsTextBox.Text = selected.Details;
    }

    private void CopyDetailsButton_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(DetailsTextBox.Text))
            Clipboard.SetText(DetailsTextBox.Text);
    }

    private void RefreshCounts(DownloadJob job)
    {
        CountText.Text = $"完成 {job.CompletedFiles} · 已有 {job.SkippedFiles} · 失败 {job.FailedFiles}";
    }

    private void SetRunningUi(bool running)
    {
        var busy = running || _isNodeTesting;
        StartButton.IsEnabled = !busy;
        StopButton.IsEnabled = running;
        RetryButton.IsEnabled = !busy && JobsGrid.SelectedItem is DownloadJob;
        InstallEngineButton.IsEnabled = !busy;
        LoginModeComboBox.IsEnabled = !busy;
        ManualCookiePanel.IsEnabled = !busy;
        OpenNodeTestButton.IsEnabled = !busy;
        ActivityProgressBar.IsIndeterminate = running;
    }

    private bool TryValidateInputs(
        string url,
        out string message,
        string? outputDirectory = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            message = "请粘贴完整的画廊链接，以 https:// 开头。";
            return false;
        }
        if (url.Contains('\n') || url.Contains('\r'))
        {
            message = "当前一次下载一条画廊链接，请先保留一条链接。";
            return false;
        }
        if (string.IsNullOrWhiteSpace(outputDirectory ?? OutputPathTextBox.Text))
        {
            message = "请选择保存位置。";
            return false;
        }
        if (UseProxyCheckBox.IsChecked == true
            && !Uri.TryCreate(NormalizeProxy(ProxyTextBox.Text), UriKind.Absolute, out _))
        {
            message = "Clash 代理地址格式不正确。通常填写 http://127.0.0.1:7897。";
            return false;
        }
        if (GetSelectedTag(LoginModeComboBox, "manual") == "manual"
            && !CookieParser.TryParse(GetManualCookie(), out _, out var cookieError))
        {
            message = cookieError;
            return false;
        }
        message = "";
        return true;
    }

    private static string NormalizeProxy(string value)
    {
        var text = value.Trim();
        if (text.Length > 0 && !text.Contains("://"))
            text = "http://" + text;
        return text;
    }

    private static string FriendlyError(string message)
    {
        if (message.Contains("proxy", StringComparison.OrdinalIgnoreCase)
            || message.Contains("actively refused", StringComparison.OrdinalIgnoreCase))
            return "连接不到当前显示的 Clash 代理地址。若已开启自动识别，程序会保留检测到的端口；也可以核对旁边的地址后重试。";
        if (message.Contains("github", StringComparison.OrdinalIgnoreCase)
            || message.Contains("name resolution", StringComparison.OrdinalIgnoreCase))
            return "无法连接内核下载服务器。请开启 Clash 后重试，必要时先在 Clash 中换一个节点。";
        return message;
    }

    private bool ConfirmBrowserCookieAccess()
    {
        var browserTag = GetSelectedTag(LoginModeComboBox, "manual");
        if (browserTag is "none" or "manual") return true;

        var processName = browserTag switch
        {
            "chrome" => "chrome",
            "edge" => "msedge",
            "firefox" => "firefox",
            _ => ""
        };
        if (processName.Length == 0)
            return true;

        var browserProcesses = Process.GetProcessesByName(processName);
        var isRunning = browserProcesses.Length > 0;
        foreach (var process in browserProcesses) process.Dispose();
        if (!isRunning)
            return true;

        var displayName = (LoginModeComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "所选浏览器";
        var answer = MessageBox.Show(
            $"{displayName} 目前仍在运行，可能会锁住登录信息文件。\n\n" +
            "如果画廊需要登录：建议先完全退出浏览器，再开始下载。\n" +
            "如果不需要登录：可选择“不读取登录信息”。\n\n" +
            "是否仍然尝试开始？",
            "浏览器仍在运行",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        return answer == MessageBoxResult.Yes;
    }

    private void LoginModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateLoginModeUi();

    private void UpdateLoginModeUi()
    {
        if (ManualCookiePanel is null || LoginModeComboBox is null) return;
        ManualCookiePanel.Visibility =
            GetSelectedTag(LoginModeComboBox, "manual") == "manual"
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private string GetManualCookie() =>
        CookieVisibleTextBox.Visibility == Visibility.Visible
            ? CookieVisibleTextBox.Text.Trim()
            : CookiePasswordBox.Password.Trim();

    private void ToggleCookieButton_Click(object sender, RoutedEventArgs e)
    {
        _syncingCookieInputs = true;
        try
        {
            if (CookieVisibleTextBox.Visibility == Visibility.Collapsed)
            {
                CookieVisibleTextBox.Text = CookiePasswordBox.Password;
                CookiePasswordBox.Visibility = Visibility.Collapsed;
                CookieVisibleTextBox.Visibility = Visibility.Visible;
                ToggleCookieButton.Content = "隐藏";
                CookieVisibleTextBox.Focus();
                CookieVisibleTextBox.CaretIndex = CookieVisibleTextBox.Text.Length;
            }
            else
            {
                CookiePasswordBox.Password = CookieVisibleTextBox.Text;
                CookieVisibleTextBox.Visibility = Visibility.Collapsed;
                CookiePasswordBox.Visibility = Visibility.Visible;
                ToggleCookieButton.Content = "显示";
                CookiePasswordBox.Focus();
            }
        }
        finally
        {
            _syncingCookieInputs = false;
        }
    }

    private async void VerifyCookieButton_Click(object sender, RoutedEventArgs e)
    {
        var cookie = GetManualCookie();
        if (!CookieParser.TryParse(cookie, out _, out var parseError))
        {
            CookieStatusText.Foreground = (Brush)FindResource("DangerBrush");
            CookieStatusText.Text = parseError;
            return;
        }

        _cookieVerificationCancellation?.Cancel();
        _cookieVerificationCancellation?.Dispose();
        _cookieVerificationCancellation = new CancellationTokenSource();
        VerifyCookieButton.IsEnabled = false;
        CookieStatusText.Foreground = (Brush)FindResource("WarningBrush");
        CookieStatusText.Text = "正在通过 Clash 验证登录，不会下载图片…";

        try
        {
            await RefreshAutoDetectedProxyAsync(
                showStatus: false,
                _cookieVerificationCancellation.Token);
            var proxy = UseProxyCheckBox.IsChecked == true ? NormalizeProxy(ProxyTextBox.Text) : null;
            var result = await CookieVerificationService.VerifyAsync(
                cookie, proxy, _cookieVerificationCancellation.Token);
            CookieStatusText.Foreground = result.EhentaiValid
                ? (Brush)FindResource("SuccessBrush")
                : (Brush)FindResource("DangerBrush");
            CookieStatusText.Text = result.Message;

            if (result.EhentaiValid && RememberCookieCheckBox.IsChecked == true)
            {
                CookiePersistence.SaveIfEnabled(cookie, enabled: true);
                _settings.RememberCookie = true;
                _settings.CookieStoragePreferenceSet = true;
                FooterStatusText.Text = "Cookie 验证成功，并已使用 Windows 当前账户加密保存。";
            }
        }
        catch (OperationCanceledException)
        {
            CookieStatusText.Foreground = (Brush)FindResource("SubTextBrush");
            CookieStatusText.Text = "验证已取消。";
        }
        catch (Exception ex)
        {
            CookieStatusText.Foreground = (Brush)FindResource("DangerBrush");
            CookieStatusText.Text = $"验证请求失败：{FriendlyError(ex.Message)}";
        }
        finally
        {
            VerifyCookieButton.IsEnabled = true;
        }
    }

    private void ClearCookieButton_Click(object sender, RoutedEventArgs e)
    {
        _syncingCookieInputs = true;
        try
        {
            CookiePasswordBox.Password = "";
            CookieVisibleTextBox.Text = "";
        }
        finally
        {
            _syncingCookieInputs = false;
        }
        _cookieSaveTimer.Stop();
        SecretStore.ClearCookie();
        CookieStatusText.Foreground = (Brush)FindResource("SubTextBrush");
        CookieStatusText.Text =
            "已清除 Cookie 和本机加密副本。自动保存仍然开启，粘贴新 Cookie 后会重新保存。";
    }

    private void CookiePasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingCookieInputs) return;
        CookieStatusText.Foreground = (Brush)FindResource("SubTextBrush");
        CookieStatusText.Text = "内容已修改，尚未验证。";
        ScheduleCookieSave();
    }

    private void CookieVisibleTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingCookieInputs) return;
        CookieStatusText.Foreground = (Brush)FindResource("SubTextBrush");
        CookieStatusText.Text = "内容已修改，尚未验证。";
        ScheduleCookieSave();
    }

    private void RememberCookieCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (RememberCookieCheckBox.IsChecked == true)
        {
            _settings.RememberCookie = true;
            _settings.CookieStoragePreferenceSet = true;
            ScheduleCookieSave();
        }
        else
        {
            _cookieSaveTimer.Stop();
            _settings.RememberCookie = false;
            _settings.CookieStoragePreferenceSet = true;
            if (!_syncingCookieInputs && CookieStatusText is not null)
            {
                CookieStatusText.Foreground = (Brush)FindResource("SubTextBrush");
                CookieStatusText.Text =
                    "已停止自动保存；已有的加密副本不会删除。如需删除，请点击“清除”。";
            }
        }
    }

    private void ScheduleCookieSave()
    {
        if (RememberCookieCheckBox.IsChecked != true
            || GetSelectedTag(LoginModeComboBox, "manual") != "manual"
            || !CookieParser.TryParse(GetManualCookie(), out _, out _))
            return;
        _cookieSaveTimer.Stop();
        _cookieSaveTimer.Start();
    }

    private void PersistManualCookie(bool showStatus)
    {
        try
        {
            if (!CookiePersistence.SaveIfEnabled(
                    GetManualCookie(),
                    RememberCookieCheckBox.IsChecked == true))
                return;
            _settings.RememberCookie = true;
            _settings.CookieStoragePreferenceSet = true;
            if (showStatus
                && CookieStatusText.Text.Contains("内容已修改", StringComparison.Ordinal))
            {
                CookieStatusText.Foreground = (Brush)FindResource("SuccessBrush");
                CookieStatusText.Text =
                    "已自动加密保存到当前 Windows 账户；内容尚未验证。";
            }
        }
        catch (Exception ex)
        {
            if (showStatus)
            {
                CookieStatusText.Foreground = (Brush)FindResource("DangerBrush");
                CookieStatusText.Text = $"自动保存失败：{ex.Message}";
            }
        }
    }

    private async Task<bool> RefreshAutoDetectedProxyAsync(
        bool showStatus,
        CancellationToken cancellationToken = default)
    {
        if (UseProxyCheckBox.IsChecked != true)
        {
            ProxyDetectionStatusText.Text = "未使用代理";
            return false;
        }
        if (AutoDetectProxyCheckBox.IsChecked != true)
        {
            ProxyDetectionStatusText.Text = "使用手动地址";
            return false;
        }

        if (_mihomoClient is not null)
        {
            try
            {
                var state = await _mihomoClient.GetStateAsync(cancellationToken);
                _clashState = state;
                ApplyDetectedProxyEndpoint(state, showStatus);
                return !string.IsNullOrWhiteSpace(state.ProxyUrl);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                _mihomoClient.Dispose();
                _mihomoClient = null;
                _clashState = null;
            }
        }

        Exception? lastError = null;
        foreach (var endpoint in new[]
                 {
                     "pipe://verge-mihomo",
                     "pipe://mihomo",
                     "http://127.0.0.1:9097",
                     "http://127.0.0.1:9090",
                     "http://127.0.0.1:9093"
                 })
        {
            MihomoClient? candidate = null;
            try
            {
                candidate = new MihomoClient(endpoint);
                using var timeout =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                var state = await candidate.GetStateAsync(timeout.Token);
                _mihomoClient = candidate;
                candidate = null;
                _clashState = state;
                await TryRecoverInterruptedNodeTestAsync();
                _clashState = await _mihomoClient.GetStateAsync(cancellationToken);
                PopulateNodeGroups();
                ApplyDetectedProxyEndpoint(_clashState, showStatus);
                return !string.IsNullOrWhiteSpace(_clashState.ProxyUrl);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                candidate?.Dispose();
                throw;
            }
            catch (Exception ex)
            {
                candidate?.Dispose();
                lastError = ex;
            }
        }

        ProxyDetectionStatusText.Text =
            $"自动识别失败，沿用 {NormalizeProxy(ProxyTextBox.Text)}";
        if (showStatus)
        {
            FooterStatusText.Text =
                "未能自动读取 Clash 端口，已继续使用输入框中的代理地址。"
                + (lastError is null ? "" : $"（{FriendlyError(lastError.Message)}）");
        }
        return false;
    }

    private void ApplyDetectedProxyEndpoint(ClashState state, bool showStatus)
    {
        if (AutoDetectProxyCheckBox.IsChecked != true
            || string.IsNullOrWhiteSpace(state.ProxyUrl))
            return;

        var previous = NormalizeProxy(ProxyTextBox.Text);
        ProxyTextBox.Text = state.ProxyUrl;
        ProxyDetectionStatusText.Text =
            $"已识别 {state.MixedPort}（{state.ProxyPortKind}）";
        if (showStatus
            && !previous.Equals(state.ProxyUrl, StringComparison.OrdinalIgnoreCase))
        {
            FooterStatusText.Text =
                $"已自动将 Clash 代理地址从 {previous} 更新为 {state.ProxyUrl}。";
        }
    }

    private async void AutoDetectProxyCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (ProxyDetectionStatusText is null) return;
        if (AutoDetectProxyCheckBox.IsChecked != true)
        {
            ProxyDetectionStatusText.Text = "使用手动地址";
            return;
        }
        ProxyDetectionStatusText.Text = "等待自动识别";
        if (IsLoaded)
            await RefreshAutoDetectedProxyAsync(showStatus: true);
    }

    private async void OpenNodeTestButton_Click(object sender, RoutedEventArgs e)
    {
        if (_runningJob is not null || _isNodeTesting) return;
        NodeTestPanel.Visibility = Visibility.Visible;
        NodeTestStatusText.Text = "正在连接 Clash/Mihomo 控制接口…";
        await EnsureNodeTesterConnectedAsync(showError: true);
    }

    private void CloseNodeTestButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isNodeTesting)
        {
            MessageBox.Show(
                "请先停止当前节点测试，软件恢复原节点后才能收起。",
                "测试仍在进行",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }
        NodeTestPanel.Visibility = Visibility.Collapsed;
    }

    private async Task<bool> EnsureNodeTesterConnectedAsync(bool showError)
    {
        var endpoints = new[]
        {
            _mihomoClient?.Endpoint,
            "pipe://verge-mihomo",
            "pipe://mihomo",
            "http://127.0.0.1:9097",
            "http://127.0.0.1:9090",
            "http://127.0.0.1:9093"
        }.Where(value => !string.IsNullOrWhiteSpace(value))
         .Distinct(StringComparer.OrdinalIgnoreCase)
         .Cast<string>()
         .ToArray();

        Exception? lastError = null;
        foreach (var endpoint in endpoints)
        {
            MihomoClient? candidate = null;
            try
            {
                candidate = new MihomoClient(endpoint);
                var state = await candidate.GetStateAsync();
                _mihomoClient?.Dispose();
                _mihomoClient = candidate;
                candidate = null;
                _clashState = state;
                await TryRecoverInterruptedNodeTestAsync();
                _clashState = await _mihomoClient.GetStateAsync();
                ApplyDetectedProxyEndpoint(_clashState, showStatus: false);
                PopulateNodeGroups();
                NodeTestStatusText.Text =
                    $"已连接 Clash · {_clashState.Mode} 模式。请选择实际控制画廊流量的策略组。";
                return true;
            }
            catch (Exception ex)
            {
                candidate?.Dispose();
                lastError = ex;
            }
        }

        _mihomoClient?.Dispose();
        _mihomoClient = null;
        _clashState = null;
        NodeGroupComboBox.ItemsSource = null;
        QuickNodeTestButton.IsEnabled = false;
        NodeCandidateText.Text = "没有连接到 Clash";
        NodeTestStatusText.Text =
            "未找到 Clash/Mihomo 控制接口。请确认 Clash Verge 正在运行。";
        if (showError)
        {
            MessageBox.Show(
                lastError?.Message ?? NodeTestStatusText.Text,
                "无法连接 Clash",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        return false;
    }

    private void PopulateNodeGroups()
    {
        if (_clashState is null) return;
        var previous = (NodeGroupComboBox.SelectedItem as ProxyInfo)?.Name;
        var groups = _clashState.Proxies.Values
            .Where(proxy => proxy.All.Count > 0
                            && proxy.Type.Equals("Selector", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(proxy =>
                proxy.Name.Contains("顶级机场", StringComparison.OrdinalIgnoreCase))
            .ThenBy(proxy => proxy.Name.Equals("GLOBAL", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenByDescending(CountDirectLeafNodes)
            .ThenBy(proxy => proxy.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        NodeGroupComboBox.ItemsSource = groups;
        NodeGroupComboBox.SelectedItem =
            groups.FirstOrDefault(group => group.Name == previous)
            ?? groups.FirstOrDefault(group =>
                group.Name.Contains("顶级机场", StringComparison.OrdinalIgnoreCase))
            ?? groups.FirstOrDefault(group =>
                !group.Name.Equals("GLOBAL", StringComparison.OrdinalIgnoreCase)
                && CountDirectLeafNodes(group) > 0)
            ?? groups.FirstOrDefault();
        UpdateNodeCandidateCount();
    }

    private int CountDirectLeafNodes(ProxyInfo group) =>
        _clashState is null
            ? 0
            : group.All.Count(name =>
                _clashState.Proxies.TryGetValue(name, out var proxy)
                && IsTestableLeafProxy(proxy));

    private List<ProxyInfo> GetNodeCandidates(ProxyInfo group)
    {
        if (_clashState is null) return [];
        return group.All
            .Select(name => _clashState.Proxies.TryGetValue(name, out var proxy) ? proxy : null)
            .Where(proxy => proxy is not null && IsTestableLeafProxy(proxy))
            .Cast<ProxyInfo>()
            .Where(proxy => !SpecialNodeTerms.Any(term =>
                proxy.Name.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .DistinctBy(proxy => proxy.Name, StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsTestableLeafProxy(ProxyInfo proxy) =>
        proxy.All.Count == 0
        && !GroupTypes.Contains(proxy.Type, StringComparer.OrdinalIgnoreCase)
        && !proxy.Name.Equals("DIRECT", StringComparison.OrdinalIgnoreCase)
        && !proxy.Name.Equals("REJECT", StringComparison.OrdinalIgnoreCase)
        && !proxy.Name.Equals("PASS", StringComparison.OrdinalIgnoreCase)
        && !proxy.Name.Equals("COMPATIBLE", StringComparison.OrdinalIgnoreCase);

    private void NodeGroupComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isNodeTesting && _resultGroup is not null
            && NodeGroupComboBox.SelectedItem is ProxyInfo selected
            && !selected.Name.Equals(_resultGroup, StringComparison.Ordinal))
        {
            NodeResults.Clear();
            _resultGroup = null;
            PreciseNodeTestButton.IsEnabled = false;
            UseTestedNodeButton.IsEnabled = false;
        }
        UpdateNodeCandidateCount();
    }

    private void UpdateNodeCandidateCount()
    {
        if (_clashState is null || NodeGroupComboBox.SelectedItem is not ProxyInfo group)
        {
            NodeCandidateText.Text = "等待连接 Clash";
            QuickNodeTestButton.IsEnabled = false;
            return;
        }
        var candidates = GetNodeCandidates(group);
        var ignored = group.All.Count - candidates.Count;
        NodeCandidateText.Text =
            $"可测 {candidates.Count} 个实际节点"
            + (ignored > 0 ? $"；忽略 {ignored} 个策略组/特殊项" : "")
            + "；先并发剔除 Error，再对候选进行小流量实测";
        QuickNodeTestButton.IsEnabled =
            !_isNodeTesting && _runningJob is null && candidates.Count > 0;
    }

    private async void QuickNodeTestButton_Click(object sender, RoutedEventArgs e)
    {
        if (_runningJob is not null || _isNodeTesting) return;
        await RefreshAutoDetectedProxyAsync(showStatus: false);
        var url = UrlTextBox.Text.Trim();
        if (!TryValidateInputs(url, out var message))
        {
            MessageBox.Show(message, "还差一点", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (UseProxyCheckBox.IsChecked != true)
        {
            MessageBox.Show(
                "真实节点测试必须勾选“使用 Clash 代理”。",
                "无法开始",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }
        if (!ConfirmBrowserCookieAccess()) return;
        if (!await EnsureNodeTesterConnectedAsync(showError: true)
            || NodeGroupComboBox.SelectedItem is not ProxyInfo group)
            return;

        var nodes = GetNodeCandidates(group);
        if (nodes.Count == 0) return;
        var testAllReachable =
            GetSelectedTag(NodeTestModeComboBox, "smart") == "all";
        if (testAllReachable && nodes.Count >= 40)
        {
            var answer = MessageBox.Show(
                $"将先用 Clash 并发剔除 Error 节点，再对所有通过画廊连通检测的节点进行真实下载测速。"
                + $"\n当前策略组共有 {nodes.Count} 个节点，完整模式可能耗时较长。\n\n"
                + "测试期间会保存并最终恢复当前节点。是否继续？",
                "确认完整检测",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
        }

        var engine = _engineManager.FindEngine();
        if (engine is null)
        {
            MessageBox.Show(
                "缺少 gallery-dl 下载内核，请先安装内核。",
                "无法开始",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        NodeResults.Clear();
        foreach (var node in nodes)
        {
            NodeResults.Add(new NodeProbeResult
            {
                Name = node.Name,
                Type = node.Type
            });
        }
        _resultGroup = group.Name;

        IReadOnlyList<NodeProbeResult> realTestCandidates = [];
        var clashReachableCount = 0;
        var galleryReachableCount = 0;
        var screeningCompleted = false;
        try
        {
            _isNodeTesting = true;
            _nodeTestCancellation = new CancellationTokenSource();
            NodeTestStatusText.Text =
                $"第一关：Clash 正在并发检测 {nodes.Count} 个节点，Error 节点会直接淘汰…";
            SetRunningUi(false);
            SetNodeTestingUi(true);
            ActivityProgressBar.IsIndeterminate = true;
            var options = CreateJob(url).Options;

            var clashDelays = await _mihomoClient!.MeasureGroupDelaysAsync(
                group.Name,
                nodes.Select(node => node.Name).ToArray(),
                new Uri("https://www.gstatic.com/generate_204"),
                timeoutMilliseconds: 3000,
                expectedStatus: "200-299",
                _nodeTestCancellation.Token);
            foreach (var result in NodeResults)
            {
                result.ClashScreened = true;
                result.ClashDelayMs =
                    clashDelays.TryGetValue(result.Name, out var delay) ? delay : 0;
                if (result.ClashDelayMs <= 0)
                {
                    result.Rating = "已淘汰";
                    result.Status = "Clash 检测 Error";
                    result.Details =
                        "第一关无法通过 Clash 的短连接测试，不再进行耗时的真实图片下载。";
                }
            }
            clashReachableCount = NodeResults.Count(result => result.ClashDelayMs > 0);
            SortNodeResults();
            if (clashReachableCount == 0)
                throw new InvalidOperationException(
                    "Clash 快速检测没有找到可连接节点。请确认所选策略组正确，或稍后重试。");

            var bootstrap = NodeResults
                .Where(result => result.ClashDelayMs > 0)
                .OrderBy(result => result.ClashDelayMs)
                .First();
            if (!_clashState!.Proxies.TryGetValue(group.Name, out var currentGroup))
                throw new InvalidOperationException("所选策略组已发生变化，请重新打开节点测速。");

            _nodeTestGroup = group.Name;
            _nodeBeforeTest = currentGroup.Now;
            await SaveNodeTestRecoveryAsync();
            NodeTestStatusText.Text =
                $"第一关完成：{nodes.Count} → {clashReachableCount}；"
                + $"正在临时使用 {bootstrap.Name} 读取画廊图片地址…";
            await _mihomoClient.SelectProxyAsync(
                group.Name, bootstrap.Name, _nodeTestCancellation.Token);
            await Task.Delay(350, _nodeTestCancellation.Token);
            _gallerySamples = await GallerySampleService.GetSampleUrlsAsync(
                engine, url, options, _nodeTestCancellation.Token);

            NodeTestStatusText.Text =
                $"第二关：正在用真实画廊图片地址并发检查 {clashReachableCount} 个节点…";
            var galleryDelays = await _mihomoClient.MeasureGroupDelaysAsync(
                group.Name,
                NodeResults
                    .Where(result => result.ClashDelayMs > 0)
                    .Select(result => result.Name)
                    .ToArray(),
                _gallerySamples[0],
                timeoutMilliseconds: 4500,
                expectedStatus: "200-399",
                _nodeTestCancellation.Token);
            foreach (var result in NodeResults.Where(result => result.ClashDelayMs > 0))
            {
                result.GalleryScreened = true;
                result.GalleryDelayMs =
                    galleryDelays.TryGetValue(result.Name, out var delay) ? delay : 0;
                if (result.GalleryDelayMs <= 0)
                {
                    result.Rating = "已淘汰";
                    result.Status = "画廊图片连接 Error";
                    result.Details =
                        "普通联网正常，但无法连接当前画廊的真实图片服务器，因此不再进行真实下载测速。";
                }
            }

            realTestCandidates = NodeScreeningLogic.SelectForRealTest(
                NodeResults,
                testAllReachable,
                NodeScreeningLogic.DefaultSmartLimit);
            galleryReachableCount = NodeResults.Count(result => result.GalleryDelayMs > 0);
            foreach (var result in NodeResults.Where(result => result.GalleryDelayMs > 0))
            {
                result.EligibleForRealTest = realTestCandidates.Contains(result);
                if (result.EligibleForRealTest)
                {
                    result.Rating = "待实测";
                    result.Status = "通过两关，等待真实测速";
                    result.Details =
                        $"Clash 延迟 {result.ClashDelayMs} ms；"
                        + $"画廊连通 {result.GalleryDelayMs} ms。";
                }
                else
                {
                    result.Rating = "候选保留";
                    result.Status = "智能模式暂不实测";
                    result.Details =
                        "两项快速检测均通过；为缩短时间，本轮不进行真实下载。"
                        + "可选择“检测全部可用节点”重新测试。";
                }
            }
            SortNodeResults();
            if (realTestCandidates.Count == 0)
                throw new InvalidOperationException(
                    "没有节点能够连接当前画廊的图片服务器。建议换一个策略组或稍后重试。");

            screeningCompleted = true;
        }
        catch (OperationCanceledException)
        {
            NodeTestStatusText.Text = "智能筛选已停止，正在恢复测试前使用的节点。";
        }
        catch (Exception ex)
        {
            NodeTestStatusText.Text = FriendlyError(ex.Message);
            MessageBox.Show(
                NodeTestStatusText.Text,
                "节点筛选未完成",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(_nodeTestGroup))
                await RestoreNodeAfterTestAsync();
            _nodeTestCancellation?.Dispose();
            _nodeTestCancellation = null;
            _isNodeTesting = false;
            ActivityProgressBar.IsIndeterminate = false;
            ActivityProgressBar.Value = 0;
            SetRunningUi(false);
            SetNodeTestingUi(false);
        }

        if (!screeningCompleted) return;
        var gallerySamples = _gallerySamples
                             ?? throw new InvalidOperationException("画廊测试地址意外丢失。");
        var summary =
            $"{nodes.Count}→{clashReachableCount}→{galleryReachableCount}→{realTestCandidates.Count}";
        NodeTestStatusText.Text =
            $"快速筛选完成（总数→联网→画廊→实测：{summary}），开始真实下载测速…";
        await RunNodeTestStageAsync(
            group.Name,
            realTestCandidates,
            gallerySamples.Take(1).ToList(),
            768L * 1024,
            4,
            $"真实快测 {summary}");
    }

    private async void PreciseNodeTestButton_Click(object sender, RoutedEventArgs e)
    {
        if (_runningJob is not null || _isNodeTesting || _gallerySamples is null
            || string.IsNullOrWhiteSpace(_resultGroup))
            return;
        var finalists = NodeResults
            .Where(result => result.SuccessfulSamples > 0)
            .OrderByDescending(result =>
                (double)result.SuccessfulSamples / Math.Max(result.SampleCount, 1))
            .ThenBy(result => result.Interruptions)
            .ThenByDescending(result => result.SpeedMbPerSecond)
            .Take(5)
            .ToList();
        if (finalists.Count == 0)
        {
            MessageBox.Show(
                "快速测试中没有成功节点。建议换一个策略组再试。",
                "没有可复测节点",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        await RunNodeTestStageAsync(
            _resultGroup,
            finalists,
            _gallerySamples.Take(3).ToList(),
            8L * 1024 * 1024,
            12,
            "精确复测");
    }

    private async Task RunNodeTestStageAsync(
        string groupName,
        IReadOnlyList<NodeProbeResult> results,
        IReadOnlyList<Uri> samples,
        long bytesPerSample,
        int timeoutSeconds,
        string stageName)
    {
        if (_mihomoClient is null || _clashState is null || samples.Count == 0) return;
        if (!_clashState.Proxies.TryGetValue(groupName, out var currentGroup))
        {
            NodeTestStatusText.Text = "所选策略组已经不存在，请重新连接 Clash。";
            return;
        }

        _nodeTestCancellation = new CancellationTokenSource();
        _nodeTestGroup = groupName;
        _nodeBeforeTest = currentGroup.Now;
        await SaveNodeTestRecoveryAsync();
        _isNodeTesting = true;
        SetRunningUi(false);
        SetNodeTestingUi(true);
        ActivityProgressBar.IsIndeterminate = false;
        ActivityProgressBar.Value = 0;
        var completedNormally = true;
        var service = new GalleryNodeTestService(
            _mihomoClient, NormalizeProxy(ProxyTextBox.Text));

        try
        {
            for (var index = 0; index < results.Count; index++)
            {
                var result = results[index];
                result.Status = $"{stageName} {index + 1}/{results.Count}";
                NodeResultsGrid.SelectedItem = result;
                NodeResultsGrid.ScrollIntoView(result);
                var progress = new Progress<string>(text =>
                {
                    NodeTestStatusText.Text =
                        $"{stageName}：{result.Name} · {text}";
                });
                try
                {
                    var measurement = await service.TestAsync(
                        groupName,
                        result.Name,
                        samples,
                        bytesPerSample,
                        timeoutSeconds,
                        progress,
                        _nodeTestCancellation.Token);
                    ApplyNodeMeasurement(result, measurement, stageName);
                }
                catch (OperationCanceledException)
                {
                    result.Status = "已停止";
                    result.Details = "测试已停止，正在恢复测试前使用的节点。";
                    completedNormally = false;
                    break;
                }
                catch (Exception ex)
                {
                    result.Status = "失败";
                    result.Rating = "不可用";
                    result.Details = FriendlyError(ex.Message);
                }

                ActivityProgressBar.Value = (index + 1d) / results.Count * 100;
                SortNodeResults();
            }
        }
        finally
        {
            await RestoreNodeAfterTestAsync();
            _nodeTestCancellation.Dispose();
            _nodeTestCancellation = null;
            _isNodeTesting = false;
            SetRunningUi(false);
            SetNodeTestingUi(false);
            SortNodeResults();

            if (!completedNormally)
            {
                foreach (var result in results.Where(result => result.Status.StartsWith("等待")))
                    result.Status = "未测试";
            }

            var best = NodeResults.FirstOrDefault(result =>
                result.SuccessfulSamples == result.SampleCount && result.SampleCount > 0);
            NodeTestStatusText.Text = completedNormally
                ? best is null
                    ? $"{stageName}完成，但没有稳定通过的节点。可以换一个策略组再试。"
                    : $"{stageName}完成，已恢复原节点。当前推荐：{best.Name}；选中后点击“使用选中节点”。"
                : "测试已停止，并已恢复测试前使用的节点。";
            PreciseNodeTestButton.IsEnabled =
                NodeResults.Any(result => result.SuccessfulSamples > 0);
            UseTestedNodeButton.IsEnabled =
                NodeResultsGrid.SelectedItem is NodeProbeResult;
        }
    }

    private static void ApplyNodeMeasurement(
        NodeProbeResult result,
        NodeProbeMeasurement measurement,
        string stageName)
    {
        result.SuccessfulSamples = measurement.SuccessfulSamples;
        result.SampleCount = measurement.SampleCount;
        result.Interruptions = measurement.Interruptions;
        result.SpeedMbPerSecond = measurement.SpeedMbPerSecond;
        result.DownloadedMb = measurement.DownloadedMb;
        result.Status = $"{stageName}完成";
        result.Rating = measurement.SuccessfulSamples == measurement.SampleCount
            ? "稳定"
            : measurement.SuccessfulSamples > 0
                ? "不稳定"
                : "不可用";
        result.Details =
            $"{stageName}：成功 {measurement.SuccessfulSamples}/{measurement.SampleCount}，"
            + $"中断 {measurement.Interruptions} 次，"
            + $"平均 {measurement.SpeedMbPerSecond:F2} MB/s，"
            + $"测试流量 {measurement.DownloadedMb:F1} MB。"
            + Environment.NewLine + measurement.Details;
    }

    private void SortNodeResults()
    {
        var sorted = NodeProbeRanking.Order(NodeResults);
        for (var target = 0; target < sorted.Count; target++)
        {
            var current = NodeResults.IndexOf(sorted[target]);
            if (current != target) NodeResults.Move(current, target);
            sorted[target].Rank =
                sorted[target].SampleCount > 0 ? target + 1 : 0;
        }

        foreach (var result in NodeResults.Where(result =>
                     result.SampleCount > 0
                     && result.SuccessfulSamples == result.SampleCount))
            result.Rating = "稳定";
        var best = NodeResults.FirstOrDefault(result =>
            result.SampleCount > 0
            && result.SuccessfulSamples == result.SampleCount);
        if (best is not null) best.Rating = "推荐";
    }

    private void SetNodeTestingUi(bool testing)
    {
        QuickNodeTestButton.IsEnabled = !testing
                                        && _runningJob is null
                                        && NodeGroupComboBox.SelectedItem is ProxyInfo;
        PreciseNodeTestButton.IsEnabled = !testing
                                          && NodeResults.Any(result =>
                                              result.SuccessfulSamples > 0);
        StopNodeTestButton.IsEnabled = testing;
        UseTestedNodeButton.IsEnabled = !testing
                                        && NodeResultsGrid.SelectedItem is NodeProbeResult
                                        {
                                            SuccessfulSamples: > 0
                                        };
        NodeGroupComboBox.IsEnabled = !testing;
        NodeTestModeComboBox.IsEnabled = !testing;
        OpenNodeTestButton.IsEnabled = !testing && _runningJob is null;
    }

    private void StopNodeTestButton_Click(object sender, RoutedEventArgs e)
    {
        StopNodeTestButton.IsEnabled = false;
        NodeTestStatusText.Text = "正在停止测试并恢复原节点…";
        _nodeTestCancellation?.Cancel();
    }

    private async void UseTestedNodeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isNodeTesting || _mihomoClient is null
            || string.IsNullOrWhiteSpace(_resultGroup)
            || NodeResultsGrid.SelectedItem is not NodeProbeResult
            {
                SuccessfulSamples: > 0
            } result)
            return;
        try
        {
            await _mihomoClient.SelectProxyAsync(_resultGroup, result.Name);
            _clashState = await _mihomoClient.GetStateAsync();
            NodeTestStatusText.Text =
                $"已将“{_resultGroup}”切换到：{result.Name}。现在可以重新尝试下载任务。";
            FooterStatusText.Text = $"已使用节点：{result.Name}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                FriendlyError(ex.Message),
                "切换节点失败",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void NodeResultsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UseTestedNodeButton.IsEnabled =
            !_isNodeTesting
            && NodeResultsGrid.SelectedItem is NodeProbeResult
            {
                SuccessfulSamples: > 0
            };
        if (!_isNodeTesting && NodeResultsGrid.SelectedItem is NodeProbeResult result)
            NodeTestStatusText.Text = result.Details;
    }

    private async Task SaveNodeTestRecoveryAsync()
    {
        if (_mihomoClient is null || string.IsNullOrWhiteSpace(_nodeTestGroup)
            || string.IsNullOrWhiteSpace(_nodeBeforeTest))
            return;
        try
        {
            Directory.CreateDirectory(SettingsStore.DataDirectory);
            var recovery = new NodeTestRecoveryState(
                _mihomoClient.Endpoint, _nodeTestGroup, _nodeBeforeTest);
            await File.WriteAllTextAsync(
                _nodeRecoveryPath, JsonSerializer.Serialize(recovery));
        }
        catch { }
    }

    private async Task TryRecoverInterruptedNodeTestAsync()
    {
        if (_mihomoClient is null || !File.Exists(_nodeRecoveryPath)) return;
        try
        {
            var recovery = JsonSerializer.Deserialize<NodeTestRecoveryState>(
                await File.ReadAllTextAsync(_nodeRecoveryPath));
            if (recovery is null
                || !recovery.Endpoint.TrimEnd('/').Equals(
                    _mihomoClient.Endpoint.TrimEnd('/'),
                    StringComparison.OrdinalIgnoreCase))
                return;
            await _mihomoClient.SelectProxyAsync(recovery.Group, recovery.Node);
            File.Delete(_nodeRecoveryPath);
            NodeTestStatusText.Text =
                $"已恢复上次意外中断前使用的节点：{recovery.Node}";
        }
        catch
        {
            // 保留恢复记录，下次连接成功后继续尝试。
        }
    }

    private async Task RestoreNodeAfterTestAsync()
    {
        if (_mihomoClient is null || string.IsNullOrWhiteSpace(_nodeTestGroup)
            || string.IsNullOrWhiteSpace(_nodeBeforeTest))
            return;
        try
        {
            await _mihomoClient.SelectProxyAsync(
                _nodeTestGroup, _nodeBeforeTest, CancellationToken.None);
            if (File.Exists(_nodeRecoveryPath)) File.Delete(_nodeRecoveryPath);
            _clashState = await _mihomoClient.GetStateAsync();
        }
        catch
        {
            NodeTestStatusText.Text =
                "暂时无法恢复原节点，已保存恢复记录；下次打开选线面板时会自动恢复。";
        }
        finally
        {
            _nodeTestGroup = null;
            _nodeBeforeTest = null;
        }
    }

    private void RunOnUi(Action action)
    {
        if (Dispatcher.CheckAccess())
            action();
        else
            _ = Dispatcher.BeginInvoke(action);
    }

    private static string GetSelectedTag(ComboBox combo, string fallback) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? fallback;

    private static void SelectComboByTag(ComboBox combo, string tag)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
        combo.SelectedIndex = 0;
    }
}
