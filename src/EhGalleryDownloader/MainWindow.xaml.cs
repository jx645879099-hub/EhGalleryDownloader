using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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
    private CancellationTokenSource? _browserImportCancellation;
    private CancellationTokenSource? _nodeTestCancellation;
    private CancellationTokenSource? _engineUpdateCancellation;
    private DownloadJob? _runningJob;
    private MihomoClient? _mihomoClient;
    private ClashState? _clashState;
    private IReadOnlyList<Uri>? _gallerySamples;
    private Func<CancellationToken, Task<IReadOnlyList<Uri>>>? _gallerySampleRefresh;
    private string? _nodeTestGroup;
    private string? _nodeBeforeTest;
    private string? _modeBeforeNodeTest;
    private string? _resultGroup;
    private bool _isNodeTesting;
    private bool _syncingCookieInputs;
    private bool _isQueueRunning;
    private bool _isPreparingResume;
    private CancellationTokenSource? _preparationCancellation;
    private bool _isPreparingNodeTest;
    private bool _isConnectingNodeTester;
    private bool _jobHistoryWritable = true;
    private ListCollectionView? _taskView;
    private readonly SemaphoreSlim _controllerConnectionGate = new(1, 1);
    private string? _nodeTestGalleryUrl;
    private bool IsOperationBusy => _runningJob is not null || _isQueueRunning
        || _isPreparingResume || _isNodeTesting || _isPreparingNodeTest || _isConnectingNodeTester
        || _engineUpdateCancellation is not null;
    private bool _stopQueueRequested;
    private bool _composerExpandedWhileBusy;
    private string? _verifiedManualCookie;

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
        App.ApplyUiScale(_settings.UiScalePercent);
        SettingsStore.CleanupStaleRunFiles();
        ApplySettings();
        try
        {
            foreach (var job in JobStore.Load()) Jobs.Add(job);
        }
        catch (InvalidDataException ex)
        {
            _jobHistoryWritable = false;
            MessageBox.Show(ex.Message + "本次运行不会覆盖原下载记录。", "记录读取失败");
        }
        _taskView = new ListCollectionView(Jobs);
        _taskView.Filter = value => value is DownloadJob job
            && (ShowCompletedTasksCheckBox.IsChecked == true || job.State != "已完成");
        JobsGrid.ItemsSource = _taskView;
        var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "未知";
        AppVersionText.Text = $"v{version} · 本地运行";
        SettingsVersionText.Text = $"当前版本 {version}";
        JobsGrid.SelectedItem = Jobs.FirstOrDefault(job => job.State != "已完成")
                                ?? Jobs.FirstOrDefault();
        RefreshCurrentTaskCard();
        UpdateComposerVisibility(false);
        ShowPage(0);
        UpdateDownloadStats();
        Loaded += async (_, _) =>
        {
            if (AutoDetectProxyCheckBox.IsChecked == true)
                await RefreshAutoDetectedProxyAsync(showStatus: false);
            await RefreshEngineStatusAsync();
            await CheckApplicationUpdateAsync(showLatest: false);
            if (!IsOperationBusy && File.Exists(_nodeRecoveryPath))
                await EnsureNodeTesterConnectedAsync(showError: false);
        };
        Closing += (_, _) =>
        {
            _preparationCancellation?.Cancel();
            _downloadCancellation?.Cancel();
            _cookieVerificationCancellation?.Cancel();
            _browserImportCancellation?.Cancel();
            _nodeTestCancellation?.Cancel();
            _engineUpdateCancellation?.Cancel();
            _service?.Dispose();
            _mihomoClient?.Dispose();
            SaveSettings();
            SaveJobs();
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
        SelectComboByTag(
            AutoFailoverModeComboBox,
            !_settings.AutoFailover ? "off" : _settings.AutoFailoverLimit > 3 ? "all" : "top3");
        MetadataCheckBox.IsChecked = _settings.WriteMetadata;
        CbzCheckBox.IsChecked = _settings.PackageAsCbz;
        SelectComboByTag(LoginModeComboBox,
            string.IsNullOrWhiteSpace(_settings.LoginMode)
                ? (_settings.UseBrowserCookies ? _settings.Browser : "manual")
                : _settings.LoginMode);
        SelectComboByTag(QualityComboBox, _settings.DownloadOriginal ? "original" : "resized");
        SelectComboByTag(UiScaleComboBox, _settings.UiScalePercent.ToString());
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
                _verifiedManualCookie = savedCookie.Trim();
                CookieStatusText.Text = _settings.LastCookieVerifiedAt is { } verifiedAt
                    ? $"已载入本机加密保存的 Cookie；上次验证：{verifiedAt:yyyy-MM-dd HH:mm}。"
                    : "已载入本机加密保存的 Cookie，建议点击“验证登录”。";
            }
        }
        UpdateLoginModeUi();
        UpdateSettingsSummary();
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
            _settings.UiScalePercent = int.TryParse(
                GetSelectedTag(UiScaleComboBox, "110"), out var uiScale)
                ? uiScale
                : 110;
            _settings.PackageAsCbz = CbzCheckBox.IsChecked == true;
            _settings.WriteMetadata = MetadataCheckBox.IsChecked == true;
            _settings.ForceIpv4 = ForceIpv4CheckBox.IsChecked == true;
            var failoverMode = GetSelectedTag(AutoFailoverModeComboBox, "top3");
            _settings.AutoFailover = failoverMode != "off";
            _settings.AutoFailoverLimit = failoverMode == "all" ? 1000 : 3;
            SettingsStore.Save(_settings);
            UpdateSettingsSummary();
        }
        catch { }
    }

    private void DownloadNavButton_Click(object sender, RoutedEventArgs e) => ShowPage(0);

    private void HistoryNavButton_Click(object sender, RoutedEventArgs e) => ShowPage(1);

    private async void NodeNavButton_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(2);
        if (!IsOperationBusy)
        {
            NodeTestStatusText.Text = "正在连接 Clash/Mihomo 控制接口…";
            await EnsureNodeTesterConnectedAsync(showError: false);
        }
    }

    private void SettingsNavButton_Click(object sender, RoutedEventArgs e) => ShowPage(3);

    private void UiScaleComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (UiScaleComboBox?.SelectedItem is not ComboBoxItem item
            || !int.TryParse(item.Tag?.ToString(), out var percent)) return;
        App.ApplyUiScale(percent);
        _settings.UiScalePercent = percent;
        SettingsStore.Save(_settings);
    }

    private void OpenSettingsButton_Click(object sender, RoutedEventArgs e) => ShowPage(3);

    private void ShowPage(int index)
    {
        if (MainNavigationTabControl is null) return;
        MainNavigationTabControl.SelectedIndex = index;
        if (index == 1 && HistoryGrid.SelectedItem is null)
        {
            HistoryGrid.SelectedItem = Jobs
                .OrderByDescending(job => job.FinishedAt ?? job.CreatedAt)
                .FirstOrDefault();
            if (HistoryGrid.SelectedItem is not null)
                HistoryGrid.ScrollIntoView(HistoryGrid.SelectedItem);
        }
        var active = new SolidColorBrush(Color.FromRgb(232, 240, 255));
        var inactive = new SolidColorBrush(Color.FromRgb(71, 84, 103));
        var activeText = new SolidColorBrush(Color.FromRgb(23, 92, 211));
        DownloadNavButton.Background = index == 0 ? active : Brushes.Transparent;
        HistoryNavButton.Background = index == 1 ? active : Brushes.Transparent;
        NodeNavButton.Background = index == 2 ? active : Brushes.Transparent;
        SettingsNavButton.Background = index == 3 ? active : Brushes.Transparent;
        DownloadNavButton.Foreground = index == 0 ? activeText : inactive;
        HistoryNavButton.Foreground = index == 1 ? activeText : inactive;
        NodeNavButton.Foreground = index == 2 ? activeText : inactive;
        SettingsNavButton.Foreground = index == 3 ? activeText : inactive;
        DownloadNavText.Foreground = DownloadNavGlyph.Foreground = index == 0 ? activeText : inactive;
        HistoryNavText.Foreground = HistoryNavGlyph.Foreground = index == 1 ? activeText : inactive;
        NodeNavText.Foreground = NodeNavGlyph.Foreground = index == 2 ? activeText : inactive;
        SettingsNavText.Foreground = SettingsNavGlyph.Foreground = index == 3 ? activeText : inactive;
        (PageTitleText.Text, PageSubtitleText.Text) = index switch
        {
            1 => ("下载记录", "查看任务结果和保存位置"),
            2 => ("连接诊断", "检查代理、节点和画廊连接是否正常"),
            3 => ("设置", "管理下载偏好、连接方式与登录信息"),
            _ => ("下载中心", "继续未完成的任务，或添加新的画廊")
        };
    }

    private void UpdateSettingsSummary()
    {
        if (CurrentSettingsSummaryText is null) return;
        var quality = GetSelectedTag(QualityComboBox, "original") == "original" ? "原图" : "压缩图";
        var failover = GetSelectedTag(AutoFailoverModeComboBox, "top3") switch
        {
            "off" => "固定当前节点",
            "all" => "自动遍历节点",
            _ => NodeResults.Any(result => result.SuccessfulSamples > 0) ? "失败后按测速换节点" : "当前节点（尚未测速）"
        };
        var metadata = MetadataCheckBox.IsChecked == true ? "保存画廊信息" : "不保存元数据";
        var package = CbzCheckBox.IsChecked == true ? "CBZ" : "文件夹";
        CurrentSettingsSummaryText.Text = $"{quality} · {failover} · {metadata} · {package}";
    }

    private void UpdateDownloadStats()
    {
        if (DownloadStatsText is null || AppStatusText is null || EmptyQueueText is null) return;
        var active = Jobs.Count(job => job.IsActive);
        var completed = Jobs.Count(job => job.State == "已完成");
        var attention = Jobs.Count(job => job.NeedsAttention);
        DownloadStatsText.Text = $"进行中 {active} · 待处理 {Jobs.Count - completed - active} · 已完成 {completed}";
        AppStatusText.Text = attention > 0 ? $"{attention} 个任务需处理" : active > 0 ? "正在下载" : "等待操作";
        EmptyQueueText.Visibility = Jobs.Any(job => ShowCompletedTasksCheckBox.IsChecked == true
            || job.State != "已完成") ? Visibility.Collapsed : Visibility.Visible;
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
                EngineStatusText.Text = "下载内核已就绪";
                EngineStatusText.ToolTip = $"gallery-dl {version}";
                InstallEngineButton.Content = "检查更新";
                if (!IsOperationBusy) FooterStatusText.Text = "选择待处理任务继续，或新建下载。";
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
        if (IsOperationBusy) return;

        _engineUpdateCancellation?.Cancel();
        _engineUpdateCancellation?.Dispose();
        _engineUpdateCancellation = new CancellationTokenSource();
        SetRunningUi(false);
        InstallEngineButton.IsEnabled = false;
        EngineStatusText.Text = "正在检查…";
        EngineDot.Fill = (Brush)FindResource("WarningBrush");
        ActivityProgressBar.Visibility = Visibility.Visible;
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
            SetRunningUi(false);
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

    private async void AppUpdateButton_Click(object sender, RoutedEventArgs e) =>
        await CheckApplicationUpdateAsync(showLatest: true);

    private async Task CheckApplicationUpdateAsync(bool showLatest)
    {
        AppUpdateButton.IsEnabled = false;
        try
        {
            var proxy = UseProxyCheckBox.IsChecked == true
                ? NormalizeProxy(ProxyTextBox.Text)
                : null;
            var update = await ApplicationUpdateService.CheckAsync(proxy);
            if (!update.IsUpdateAvailable)
            {
                if (showLatest)
                    MessageBox.Show(
                        $"当前软件版本 {update.CurrentVersion.ToString(3)} 已是最新版。",
                        "软件已是最新版",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                return;
            }

            FooterStatusText.Text = $"发现软件新版本 {update.Tag}。";
            var answer = MessageBox.Show(
                $"发现软件新版本。\n\n当前版本：{update.CurrentVersion.ToString(3)}"
                + $"\n最新版本：{update.Tag}\n\n是否打开下载页面？",
                "软件更新",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            if (answer == MessageBoxResult.Yes)
                Process.Start(new ProcessStartInfo
                {
                    FileName = update.ReleaseUrl,
                    UseShellExecute = true
                });
        }
        catch (Exception ex)
        {
            if (showLatest)
                MessageBox.Show(
                    FriendlyError(ex.GetBaseException().Message),
                    "检查软件更新失败",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
        }
        finally
        {
            AppUpdateButton.IsEnabled = true;
        }
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
        if (IsOperationBusy) return;
        BeginDownloadPreparation("正在检查连接，准备新任务…");
        try
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            await RefreshAutoDetectedProxyAsync(showStatus: false, _preparationCancellation!.Token);
            var urls = GalleryUrlValidator.ParseMany(UrlTextBox.Text, out var message);
            if (urls.Count == 0)
            {
                MessageBox.Show(message, "还差一点", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!TryValidateInputs(urls[0], out message))
            {
                MessageBox.Show(message, "还差一点", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            foreach (var url in urls.Skip(1))
            {
                if (TryValidateInputs(url, out message)) continue;
                MessageBox.Show(message, "还差一点", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!ConfirmBrowserCookieAccess())
                return;

            var queued = new List<DownloadJob>();
            var newJobInsertIndex = 0;
            foreach (var url in urls)
            {
                var job = DownloadTaskLogic.FindReusable(Jobs, url) ?? CreateJob(url);
                if (!Jobs.Contains(job))
                    Jobs.Insert(newJobInsertIndex++, job);
                else
                {
                    RefreshRetryOptions(job);
                    job.OutputDirectory = OutputPathTextBox.Text.Trim();
                }
                job.State = "排队中";
                job.Details = "已加入下载队列。";
                queued.Add(job);
            }
            SaveJobs();
            UrlTextBox.Clear();
            JobsGrid.SelectedItem = queued[0];
            await RunQueuedJobsAsync(queued);
        }
        catch (OperationCanceledException) { FooterStatusText.Text = "已取消准备。"; }
        catch (Exception ex) { FooterStatusText.Text = FriendlyError(ex.Message); }
        finally { EndDownloadPreparation(); }
    }

    private void BeginDownloadPreparation(string message)
    {
        _isPreparingResume = true;
        _preparationCancellation = new CancellationTokenSource();
        FooterStatusText.Text = message;
        SetRunningUi(false);
    }

    private void EndDownloadPreparation()
    {
        _isPreparingResume = false;
        _preparationCancellation?.Dispose();
        _preparationCancellation = null;
        SetRunningUi(_runningJob is not null);
    }

    private async Task RunQueuedJobsAsync(IEnumerable<DownloadJob>? requestedJobs = null)
    {
        if (_isQueueRunning || _runningJob is not null) return;
        var queue = (requestedJobs ?? Jobs.Where(job => job.State == "排队中")).ToList();
        _isQueueRunning = true;
        _stopQueueRequested = false;
        SetRunningUi(false);
        try
        {
            foreach (var next in queue)
            {
                if (_stopQueueRequested) break;
                if (next.State != "排队中") continue;
                JobsGrid.SelectedItem = next;
                RefreshRetryOptions(next);
                await RunJobAsync(next);
                SaveJobs();
            }
        }
        finally
        {
            if (_stopQueueRequested)
                foreach (var pending in queue.Where(job => job.State == "排队中"))
                {
                    pending.State = "已停止";
                    pending.Details = "队列已暂停，点击继续下载可恢复。";
                }
            _isQueueRunning = false;
            SaveJobs();
            SetRunningUi(false);
        }
    }

    private async void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsOperationBusy
            || JobsGrid.SelectedItem is not DownloadJob selected) return;
        var previousState = selected.State;
        BeginDownloadPreparation("正在准备继续下载…");
        selected.State = "准备中";
        selected.Details = "已收到继续请求，正在检查连接并定位本地断点…";
        FooterStatusText.Text = selected.Details;
        RefreshSelectedDetails();
        try
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            await RefreshAutoDetectedProxyAsync(showStatus: false, _preparationCancellation!.Token);
            if (!TryValidateInputs(selected.Url, out var message, selected.OutputDirectory))
            {
                MessageBox.Show(message, "无法继续", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!ConfirmBrowserCookieAccess()) return;
            RefreshRetryOptions(selected);
            await RunJobAsync(selected);
        }
        catch (OperationCanceledException) { selected.Details = "已取消准备，已有图片已保留。"; }
        catch (Exception ex) { selected.Details = FriendlyError(ex.Message); }
        finally
        {
            if (selected.State == "准备中") selected.State = previousState;
            EndDownloadPreparation();
            SaveJobs();
        }
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

        _preparationCancellation?.Token.ThrowIfCancellationRequested();
        SaveSettings();
        job.BeginAttempt();
        _runningJob = job;
        _downloadCancellation = new CancellationTokenSource();
        _service = new GalleryDlService();
        job.State = "下载中";
        job.Details = job.AttemptCount > 1
            ? $"正在继续第 {job.AttemptCount} 次尝试；已有完整文件会直接跳过。"
            : "正在读取画廊信息，请稍候。首次读取登录信息可能需要几秒钟。";
        SaveJobs();
        SetRunningUi(true);
        CountText.Text = "";

        _service.CurrentFileChanged += file => RunOnUi(() =>
        {
            job.CurrentFile = file;
            job.Details = $"正在处理：{file}";
            RefreshSelectedDetails();
        });
        _service.FileCompleted += delta => RunOnUi(() =>
        {
            job.CompletedFiles = Math.Max(0, job.CompletedFiles + delta);
            RefreshCounts(job);
        });
        _service.FileSkipped += delta => RunOnUi(() =>
        {
            job.SkippedFiles = Math.Max(0, job.SkippedFiles + delta);
            RefreshCounts(job);
        });
        _service.FileFailed += delta => RunOnUi(() =>
        {
            job.FailedFiles = Math.Max(0, job.FailedFiles + delta);
            RefreshCounts(job);
        });
        _service.OutputReceived += line => RunOnUi(() =>
        {
            job.Details = line;
            RefreshSelectedDetails();
        });
        _service.GalleryMetadataChanged += (title, total) => RunOnUi(() =>
        {
            job.GalleryTitle = title;
            if (total > 0) job.TotalFiles = total;
            RefreshCurrentTaskCard();
        });
        _service.ExistingPrefixDetected += count => RunOnUi(() =>
        {
            job.SkippedFiles = Math.Max(job.SkippedFiles, count);
            job.Details = $"已在本地确认前 {count} 张完整图片，正在从第 {count + 1} 张继续。";
            RefreshCounts(job);
            RefreshSelectedDetails();
        });
        _service.ProgressChanged += progress => RunOnUi(() =>
        {
            if (progress.Total > 0)
                job.TotalFiles = Math.Max(
                    job.TotalFiles,
                    Math.Max(progress.Total, job.CompletedFiles + job.SkippedFiles));
            job.SpeedMbPerSecond = progress.SpeedMbPerSecond;
            job.EstimatedRemaining = progress.EstimatedRemaining is { } remaining
                ? remaining.TotalHours >= 1
                    ? remaining.ToString(@"h\:mm\:ss")
                    : remaining.ToString(@"m\:ss")
                : "—";
            RefreshCounts(job);
        });

        try
        {
            var attemptedNodes = new HashSet<string>(StringComparer.Ordinal);
            var automaticSwitches = 0;
            var compatibilityResume = false;
            var automaticSwitchLimit = GetSelectedTag(
                AutoFailoverModeComboBox, "top3") == "all"
                ? int.MaxValue
                : 3;
            while (true)
            {
                var exitCode = await _service.RunAsync(
                    engine, job, _downloadCancellation.Token, compatibilityResume);
                await Dispatcher.Yield(DispatcherPriority.Background);
                if (_downloadCancellation.IsCancellationRequested || exitCode == -1)
                {
                    job.FinishedAt = DateTime.Now;
                    job.State = "已停止";
                    job.Details = "任务已停止。已完成的图片会保留；下次重新尝试时会跳过成品，并续传 .part 临时文件。";
                    FooterStatusText.Text = "任务已停止，可随时选中后重新尝试。";
                    break;
                }
                if (exitCode == 0 && job.FailedFiles == 0)
                {
                    job.FinishedAt = DateTime.Now;
                    job.State = "已完成";
                    job.Details = job.SkippedFiles > 0
                        ? $"下载完成。本次新完成 {job.CompletedFiles} 个文件，已有并跳过 {job.SkippedFiles} 个文件。"
                        : $"下载完成，共完成 {job.CompletedFiles} 个文件。";
                    FooterStatusText.Text = "下载完成。";
                    break;
                }
                if (!compatibilityResume
                    && _service.LastRunUsedImageAnchor
                    && GalleryResumePlanner.IsImageAnchorUnavailable(_service.LastRawError))
                {
                    compatibilityResume = true;
                    job.BeginAttempt();
                    job.State = "自动续传";
                    job.Details = "快速断点入口失效，已自动改用兼容续传；定位旧页期间可能较慢。";
                    FooterStatusText.Text = job.Details;
                    SaveJobs();
                    continue;
                }
                if (_service.LastRawError.Contains(
                        "KeyError - 'i3'", StringComparison.OrdinalIgnoreCase))
                {
                    job.FinishedAt = DateTime.Now;
                    job.State = "需要更新内核";
                    job.BlockedEngineVersion = await _engineManager.GetVersionAsync();
                    job.Details =
                        "下载内核兼容错误（KeyError i3）。本次任务没有继续运行；"
                        + "请先更新内核，再点击“继续 / 补漏选中任务”。重复使用当前内核不会改善。";
                    FooterStatusText.Text = "任务需要先更新下载内核，已阻止无意义的重复尝试。";
                    break;
                }

                if (automaticSwitches < automaticSwitchLimit
                    && await TrySwitchToNextTestedNodeAsync(
                        _service.LastRawError, attemptedNodes))
                {
                    automaticSwitches++;
                    job.BeginAttempt();
                    job.State = "自动续传";
                    job.Details = $"网络故障后已自动切换节点，正在进行第 {job.AttemptCount} 次续传。";
                    FooterStatusText.Text =
                        automaticSwitchLimit == int.MaxValue
                            ? $"已自动换节点续传（第 {automaticSwitches} 次）。"
                            : $"已自动换节点续传（{automaticSwitches}/{automaticSwitchLimit}）。";
                    SaveJobs();
                    continue;
                }

                job.FinishedAt = DateTime.Now;
                job.State = "失败";
                if (!job.Details.Contains("失败") && !job.Details.Contains("错误"))
                    job.Details =
                        $"下载没有完整完成（内核返回代码 {exitCode}）。"
                        + "可复制说明，或换节点后点“继续 / 补漏选中任务”。";
                FooterStatusText.Text = "任务没有完整完成，可以换节点后重新尝试。";
                break;
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
            job.SpeedMbPerSecond = 0;
            job.EstimatedRemaining = "—";
            _service.Dispose();
            _service = null;
            _downloadCancellation.Dispose();
            _downloadCancellation = null;
            _runningJob = null;
            SetRunningUi(false);
            RefreshCounts(job);
            RefreshSelectedDetails();
            SaveJobs();
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _preparationCancellation?.Cancel();
        StopButton.IsEnabled = false;
        FooterStatusText.Text = "正在安全停止；已经下完的文件不会删除。";
        _downloadCancellation?.Cancel();
        _service?.Stop();
        _stopQueueRequested = true;
    }

    private async void ContinueAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsOperationBusy) return;
        var firstPending = Jobs.FirstOrDefault(job => job.State != "已完成");
        if (firstPending is null)
        {
            MessageBox.Show("没有需要继续的任务。", "队列为空",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        BeginDownloadPreparation("正在检查连接和待续任务…");
        try
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            await RefreshAutoDetectedProxyAsync(showStatus: false, _preparationCancellation!.Token);
            if (!TryValidateInputs(firstPending.Url, out var message, firstPending.OutputDirectory))
            {
                MessageBox.Show(message, "无法继续队列", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            foreach (var job in Jobs.Where(job => job.State != "已完成"))
            {
                if (TryValidateInputs(job.Url, out message, job.OutputDirectory)) continue;
                MessageBox.Show(message, "无法继续队列", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (!ConfirmBrowserCookieAccess()) return;
            var queue = Jobs.Where(job => job.State is not "已完成" and not "下载中")
                .ToList();
            foreach (var job in queue)
            {
                RefreshRetryOptions(job);
                job.State = "排队中";
                job.Details = "已加入继续下载队列。";
            }
            SaveJobs();
            await RunQueuedJobsAsync(queue);
        }
        catch (OperationCanceledException) { FooterStatusText.Text = "已取消准备。"; }
        catch (Exception ex) { FooterStatusText.Text = FriendlyError(ex.Message); }
        finally
        {
            EndDownloadPreparation();
        }
    }

    private void RemoveSelectedJobButton_Click(object sender, RoutedEventArgs e)
    {
        JobMorePopup.IsOpen = false;
        if (IsOperationBusy) return;
        if (JobsGrid.SelectedItem is not DownloadJob selected
            || ReferenceEquals(selected, _runningJob)) return;
        Jobs.Remove(selected);
        SaveJobs();
    }

    private void ClearFinishedJobsButton_Click(object sender, RoutedEventArgs e)
    {
        JobMorePopup.IsOpen = false;
        if (IsOperationBusy) return;
        foreach (var job in Jobs.Where(job => job.State == "已完成").ToList())
            Jobs.Remove(job);
        SaveJobs();
    }

    private void MoveSelectedJob(int offset)
    {
        if (IsOperationBusy) return;
        if (JobsGrid.SelectedItem is not DownloadJob selected
            || ReferenceEquals(selected, _runningJob)) return;
        var oldIndex = Jobs.IndexOf(selected);
        var newIndex = Math.Clamp(oldIndex + offset, 0, Jobs.Count - 1);
        if (oldIndex == newIndex) return;
        Jobs.Move(oldIndex, newIndex);
        JobsGrid.SelectedItem = selected;
        SaveJobs();
    }

    private void MoveJobUpButton_Click(object sender, RoutedEventArgs e)
    {
        JobMorePopup.IsOpen = false;
        MoveSelectedJob(-1);
    }

    private void MoveJobDownButton_Click(object sender, RoutedEventArgs e)
    {
        JobMorePopup.IsOpen = false;
        MoveSelectedJob(1);
    }

    private void JobMoreButton_Click(object sender, RoutedEventArgs e)
    {
        JobMorePopup.IsOpen = !JobMorePopup.IsOpen;
    }

    private void ToggleTaskDetailsButton_Click(object sender, RoutedEventArgs e)
    {
        TaskDetailsCard.Visibility = TaskDetailsCard.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (sender is Button button)
            button.Content = TaskDetailsCard.Visibility == Visibility.Visible
                ? "收起任务详情  ⌃"
                : "展开任务详情  ⌄";
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var path = CurrentTaskCard.DataContext is DownloadJob selected
            ? selected.OutputDirectory
            : OutputPathTextBox.Text.Trim();
        OpenDirectory(path);
    }

    private static void OpenDirectory(string? directory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException("没有可打开的保存位置。");

            var path = Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(directory.Trim()));
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法打开保存位置：{ex.GetBaseException().Message}", "打开失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void JobsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RetryButton.IsEnabled = !IsOperationBusy && JobsGrid.SelectedItem is DownloadJob;
        RefreshCurrentTaskCard();
        RefreshSelectedDetails();
    }

    private void AddNewTaskCollapsedButton_Click(object sender, RoutedEventArgs e)
    {
        _composerExpandedWhileBusy = true;
        UpdateComposerVisibility(_runningJob is not null || _isNodeTesting || _isQueueRunning);
        UrlTextBox.Focus();
    }

    private void UpdateComposerVisibility(bool busy)
    {
        if (NewTaskCard is null || AddNewTaskCollapsedButton is null) return;
        CloseNewTaskButton.Visibility = Jobs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        var collapse = (busy || Jobs.Count > 0) && !_composerExpandedWhileBusy;
        NewTaskCard.Visibility = collapse ? Visibility.Collapsed : Visibility.Visible;
        AddNewTaskCollapsedButton.Visibility = collapse ? Visibility.Visible : Visibility.Collapsed;
        RefreshCurrentTaskCard();
    }

    private void CloseNewTaskButton_Click(object sender, RoutedEventArgs e)
    {
        _composerExpandedWhileBusy = false;
        UpdateComposerVisibility(IsOperationBusy);
    }

    private void RefreshCurrentTaskCard()
    {
        if (CurrentTaskCard is null) return;
        var job = _runningJob ?? JobsGrid?.SelectedItem as DownloadJob
            ?? Jobs.FirstOrDefault(item => ShowCompletedTasksCheckBox.IsChecked == true
                || item.State != "已完成");
        CurrentTaskCard.DataContext = job;
        CurrentTaskCard.Visibility = job is null || NewTaskCard.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;
        CurrentTaskHeadingText.Text = _runningJob is not null ? "正在进行" : "选中任务";
        if (CurrentTaskNodeText is not null)
            CurrentTaskNodeText.Text = GetCurrentNodeName();
    }

    private string GetCurrentNodeName()
    {
        if (_clashState is not null
            && !string.IsNullOrWhiteSpace(_resultGroup)
            && _clashState.Proxies.TryGetValue(_resultGroup, out var group)
            && !string.IsNullOrWhiteSpace(group.Now))
            return group.Now;
        return UseProxyCheckBox.IsChecked == true ? "Clash 当前节点" : "直接连接";
    }

    private void HistoryOpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = HistoryGrid.SelectedItem as DownloadJob
                       ?? JobsGrid.SelectedItem as DownloadJob
                       ?? Jobs.OrderByDescending(job => job.FinishedAt ?? job.CreatedAt)
                           .FirstOrDefault();
        OpenDirectory(selected?.OutputDirectory);
    }

    private void RefreshSelectedDetails()
    {
        if (CurrentTaskCard.DataContext is DownloadJob selected)
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
        var busy = running || IsOperationBusy;
        if (busy) _composerExpandedWhileBusy = false;
        UpdateComposerVisibility(busy);
        StartButton.IsEnabled = !busy;
        StopButton.IsEnabled = running || _isPreparingResume;
        StopButton.Visibility = running || _isPreparingResume ? Visibility.Visible : Visibility.Collapsed;
        RetryButton.Visibility = running || _isPreparingResume ? Visibility.Collapsed : Visibility.Visible;
        StopButton.Content = running ? "暂停下载" : "取消准备";
        RetryButton.IsEnabled = !busy && JobsGrid.SelectedItem is DownloadJob;
        InstallEngineButton.IsEnabled = !busy;
        SettingsEngineUpdateButton.IsEnabled = !busy;
        ContinueAllButton.IsEnabled = !busy && Jobs.Any(job => job.State != "已完成");
        JobMoreButton.IsEnabled = !busy;
        SettingsTabControl.IsEnabled = !busy;
        AddNewTaskCollapsedButton.IsEnabled = !busy;
        QuickNodeTestButton.IsEnabled = !busy && NodeGroupComboBox.SelectedItem is ProxyInfo;
        PreciseNodeTestButton.IsEnabled = !busy && NodeResults.Any(result => result.SuccessfulSamples > 0);
        UseTestedNodeButton.IsEnabled = !busy && NodeResultsGrid.SelectedItem is NodeProbeResult { SuccessfulSamples: > 0 };
        NodeGroupComboBox.IsEnabled = !busy;
        NodeTestModeComboBox.IsEnabled = !busy;
        LoginModeComboBox.IsEnabled = !busy;
        ManualCookiePanel.IsEnabled = !busy
                                      && GetSelectedTag(LoginModeComboBox, "manual") != "none";
        ImportBrowserCookieButton.IsEnabled = !busy && _browserImportCancellation is null;
        OpenExtensionSetupButton.IsEnabled = !busy && _browserImportCancellation is null;
        OpenNodeTestButton.IsEnabled = !busy;
        AutoFailoverModeComboBox.IsEnabled = !busy;
        ActivityProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ActivityProgressBar.IsIndeterminate = running || _isPreparingResume || _isPreparingNodeTest;
        RefreshCurrentTaskCard();
    }

    private void ShowCompletedTasksCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        _taskView?.Refresh();
        UpdateDownloadStats();
        if (JobsGrid.SelectedItem is null) JobsGrid.SelectedItem = _taskView?.Cast<DownloadJob>().FirstOrDefault();
        RefreshCurrentTaskCard();
    }

    private void HistoryContinueButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsOperationBusy || HistoryGrid.SelectedItem is not DownloadJob selected) return;
        ShowCompletedTasksCheckBox.IsChecked = selected.State == "已完成";
        ShowPage(0);
        JobsGrid.SelectedItem = selected;
        RetryButton_Click(sender, e);
    }

    private bool TryValidateInputs(
        string url,
        out string message,
        string? outputDirectory = null)
    {
        if (!GalleryUrlValidator.TryNormalize(url, out _, out message))
        {
            return false;
        }
        var selectedOutput = (outputDirectory ?? OutputPathTextBox.Text).Trim();
        if (string.IsNullOrWhiteSpace(selectedOutput))
        {
            message = "请选择保存位置。";
            return false;
        }
        try
        {
            Directory.CreateDirectory(Path.GetFullPath(selectedOutput));
        }
        catch (Exception ex)
        {
            message = $"保存位置无法使用：{ex.GetBaseException().Message}";
            return false;
        }
        if (UseProxyCheckBox.IsChecked == true
            && (!Uri.TryCreate(NormalizeProxy(ProxyTextBox.Text), UriKind.Absolute, out var proxyUri)
                || proxyUri.Scheme is not ("http" or "https" or "socks4" or "socks5")))
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

    private async Task<bool> TrySwitchToNextTestedNodeAsync(
        string error,
        HashSet<string> attemptedNodes)
    {
        if (GetSelectedTag(AutoFailoverModeComboBox, "top3") == "off"
            || !DownloadErrorClassifier.IsTransient(error)
            || _mihomoClient is null
            || string.IsNullOrWhiteSpace(_resultGroup))
            return false;

        try
        {
            var state = await _mihomoClient.GetStateAsync();
            if (state.Proxies.TryGetValue(_resultGroup, out var group)
                && !string.IsNullOrWhiteSpace(group.Now))
                attemptedNodes.Add(group.Now);
            var next = NodeProbeRanking.Order(NodeResults)
                .FirstOrDefault(result => result.SuccessfulSamples > 0
                                          && !attemptedNodes.Contains(result.Name));
            if (next is null) return false;
            attemptedNodes.Add(next.Name);
            await _mihomoClient.SelectProxyAsync(_resultGroup, next.Name);
            _clashState = await _mihomoClient.GetStateAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void SaveJobs()
    {
        UpdateDownloadStats();
        _taskView?.Refresh();
        UpdateComposerVisibility(_runningJob is not null || _isNodeTesting || _isQueueRunning);
        if (JobsGrid.SelectedItem is null) JobsGrid.SelectedItem = _taskView?.Cast<DownloadJob>().FirstOrDefault();
        if (!_jobHistoryWritable) return;
        try { JobStore.Save(Jobs); }
        catch { }
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
        // 自动导入与手动填写是并列入口。即使用户选择浏览器资料读取，
        // 也始终保留手动 Cookie，避免用户被锁死在单一登录方式里。
        ManualCookiePanel.Visibility = Visibility.Visible;
        ManualCookiePanel.IsEnabled = GetSelectedTag(LoginModeComboBox, "manual") != "none";
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

        await VerifyAndStoreCookieAsync(cookie, "手动填写");
    }

    private async void ImportBrowserCookieButton_Click(object sender, RoutedEventArgs e)
    {
        if (_browserImportCancellation is not null)
        {
            _browserImportCancellation.Cancel();
            return;
        }

        var consent = new BrowserCookieConsentWindow { Owner = this };
        if (consent.ShowDialog() != true) return;

        _browserImportCancellation = new CancellationTokenSource();
        ImportBrowserCookieButton.Content = "取消等待";
        OpenExtensionSetupButton.IsEnabled = false;
        CookieStatusText.Foreground = (Brush)FindResource("WarningBrush");
        CookieStatusText.Text =
            "正在等待浏览器…请切换到已登录的画廊网页，点击工具栏中的“画廊登录导入”扩展。";

        try
        {
            await using var server = BrowserCookieImportServer.Start();
            var imported = await server.WaitForImportAsync(
                TimeSpan.FromMinutes(2), _browserImportCancellation.Token);

            SelectComboByTag(LoginModeComboBox, "manual");
            _syncingCookieInputs = true;
            CookiePasswordBox.Password = imported.CookieHeader;
            CookieVisibleTextBox.Text = imported.CookieHeader;
            _syncingCookieInputs = false;
            UpdateLoginModeUi();
            CookieStatusText.Text = $"已从 {imported.Browser} 收到登录状态，正在验证…";
            await VerifyAndStoreCookieAsync(imported.CookieHeader, imported.Browser);
        }
        catch (OperationCanceledException)
        {
            CookieStatusText.Foreground = (Brush)FindResource("SubTextBrush");
            CookieStatusText.Text = "已取消等待浏览器导入。";
        }
        catch (TimeoutException ex)
        {
            CookieStatusText.Foreground = (Brush)FindResource("WarningBrush");
            CookieStatusText.Text = ex.Message;
        }
        catch (Exception ex)
        {
            CookieStatusText.Foreground = (Brush)FindResource("DangerBrush");
            CookieStatusText.Text = FriendlyError(ex.Message);
        }
        finally
        {
            _browserImportCancellation.Dispose();
            _browserImportCancellation = null;
            ImportBrowserCookieButton.Content = "从当前网页导入";
            OpenExtensionSetupButton.IsEnabled = true;
        }
    }

    private void OpenExtensionSetupButton_Click(object sender, RoutedEventArgs e)
    {
        var extensionDirectory = Path.Combine(AppContext.BaseDirectory, "BrowserExtension");
        if (!Directory.Exists(extensionDirectory))
        {
            MessageBox.Show(
                "当前程序目录中缺少 BrowserExtension 文件夹，请重新解压完整安装包。",
                "没有找到扩展文件",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = extensionDirectory,
            UseShellExecute = true
        });
        OpenInstalledBrowserExtensionsPage();
        MessageBox.Show(
            "已打开扩展文件夹和浏览器扩展页面。\n\n" +
            "开启“开发者模式” → “加载已解压的扩展程序” → 选择刚打开的 BrowserExtension 文件夹。\n" +
            "Edge 用户请在地址栏打开 edge://extensions/，操作相同。",
            "首次安装扩展",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private static void OpenInstalledBrowserExtensionsPage()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var chromeCandidates = new[]
        {
            Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe")
        };
        var edgeCandidates = new[]
        {
            Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe")
        };

        var chromeProcesses = Process.GetProcessesByName("chrome");
        var chromeRunning = chromeProcesses.Length > 0;
        foreach (var process in chromeProcesses) process.Dispose();
        var ordered = chromeRunning
            ? chromeCandidates.Select(path => (path, "chrome://extensions/"))
                .Concat(edgeCandidates.Select(path => (path, "edge://extensions/")))
            : edgeCandidates.Select(path => (path, "edge://extensions/"))
                .Concat(chromeCandidates.Select(path => (path, "chrome://extensions/")));
        var selected = ordered.FirstOrDefault(item => File.Exists(item.path));
        if (!string.IsNullOrWhiteSpace(selected.path))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = selected.path,
                Arguments = selected.Item2,
                UseShellExecute = true
            });
        }
    }

    private async Task<bool> VerifyAndStoreCookieAsync(string cookie, string sourceLabel)
    {
        _cookieVerificationCancellation?.Cancel();
        _cookieVerificationCancellation?.Dispose();
        _cookieVerificationCancellation = new CancellationTokenSource();
        VerifyCookieButton.IsEnabled = false;
        ImportBrowserCookieButton.IsEnabled = false;
        CookieStatusText.Foreground = (Brush)FindResource("WarningBrush");
        CookieStatusText.Text = $"正在验证来自{sourceLabel}的登录状态，不会下载图片…";

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

            if (!result.EhentaiValid) return false;

            _verifiedManualCookie = cookie.Trim();
            _settings.LastCookieVerifiedAt = DateTime.Now;
            if (RememberCookieCheckBox.IsChecked == true)
            {
                CookiePersistence.SaveIfEnabled(cookie, enabled: true);
                _settings.RememberCookie = true;
                _settings.CookieStoragePreferenceSet = true;
                FooterStatusText.Text =
                    $"已从{sourceLabel}导入并验证，登录状态已使用 Windows 当前账户加密保存。";
            }
            SettingsStore.Save(_settings);
            return true;
        }
        catch (OperationCanceledException)
        {
            CookieStatusText.Foreground = (Brush)FindResource("SubTextBrush");
            CookieStatusText.Text = "验证已取消。";
            return false;
        }
        catch (Exception ex)
        {
            CookieStatusText.Foreground = (Brush)FindResource("DangerBrush");
            CookieStatusText.Text = $"验证请求失败：{FriendlyError(ex.Message)}";
            return false;
        }
        finally
        {
            VerifyCookieButton.IsEnabled = true;
            ImportBrowserCookieButton.IsEnabled = true;
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
        _verifiedManualCookie = null;
        _settings.LastCookieVerifiedAt = null;
        CookieStatusText.Foreground = (Brush)FindResource("SubTextBrush");
        CookieStatusText.Text =
            "已清除 Cookie 和本机加密副本。自动保存仍然开启，粘贴新 Cookie 后会重新保存。";
    }

    private void CookiePasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingCookieInputs) return;
        if (!string.Equals(GetManualCookie(), _verifiedManualCookie, StringComparison.Ordinal))
            _settings.LastCookieVerifiedAt = null;
        CookieStatusText.Foreground = (Brush)FindResource("SubTextBrush");
        CookieStatusText.Text = "内容已修改，尚未验证。";
        ScheduleCookieSave();
    }

    private void CookieVisibleTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingCookieInputs) return;
        if (!string.Equals(GetManualCookie(), _verifiedManualCookie, StringComparison.Ordinal))
            _settings.LastCookieVerifiedAt = null;
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
            || !string.Equals(GetManualCookie(), _verifiedManualCookie, StringComparison.Ordinal)
            || !CookieParser.TryParse(GetManualCookie(), out _, out _))
            return;
        _cookieSaveTimer.Stop();
        _cookieSaveTimer.Start();
    }

    private void PersistManualCookie(bool showStatus)
    {
        try
        {
            if (!string.Equals(GetManualCookie(), _verifiedManualCookie, StringComparison.Ordinal))
                return;
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
        await _controllerConnectionGate.WaitAsync(cancellationToken);
        try
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
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(3));
                    var state = await _mihomoClient.GetStateAsync(timeout.Token);
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
        finally { _controllerConnectionGate.Release(); }
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
        if (IsOperationBusy) return;
        ShowPage(2);
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
        ShowPage(0);
    }

    private async Task<bool> EnsureNodeTesterConnectedAsync(bool showError)
    {
        if (_isConnectingNodeTester) return false;
        _isConnectingNodeTester = true;
        SetRunningUi(false);
        await _controllerConnectionGate.WaitAsync();
        try
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
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    var state = await candidate.GetStateAsync(deadline.Token);
                    _mihomoClient?.Dispose();
                    _mihomoClient = candidate;
                    candidate = null;
                    _clashState = state;
                    await TryRecoverInterruptedNodeTestAsync();
                    if (File.Exists(_nodeRecoveryPath))
                    {
                        NodeTestStatusText.Text = "上次测速的节点尚未恢复，请先在 Clash 检查节点和模式，再重新连接。";
                        return false;
                    }
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
        finally
        {
            _controllerConnectionGate.Release();
            _isConnectingNodeTester = false;
            SetRunningUi(false);
        }
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
            _gallerySamples = null;
            _gallerySampleRefresh = null;
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
            + "；先用 Clash 剔除断线节点，再进行真实图片下载测速";
        QuickNodeTestButton.IsEnabled =
            !IsOperationBusy && candidates.Count > 0;
    }

    private async void QuickNodeTestButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsOperationBusy) return;
        _isPreparingNodeTest = true;
        SetRunningUi(false);
        try
        {
            await RefreshAutoDetectedProxyAsync(showStatus: false);
            var url = string.IsNullOrWhiteSpace(UrlTextBox.Text)
                ? (JobsGrid.SelectedItem as DownloadJob)?.Url ?? Jobs.FirstOrDefault()?.Url ?? ""
                : GalleryUrlValidator.ParseMany(UrlTextBox.Text, out _).FirstOrDefault() ?? UrlTextBox.Text.Trim();
            _nodeTestGalleryUrl = url;
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
                    $"将先用 Clash 并发剔除断线节点，再对所有可联网节点进行真实图片下载测速。"
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
            _gallerySamples = null;
            _gallerySampleRefresh = null;
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
            var screeningCompleted = false;
            var options = CreateJob(url).Options;
            try
            {
                _isNodeTesting = true;
                _nodeTestCancellation = new CancellationTokenSource();
                NodeTestStatusText.Text =
                    $"第一关：正在要求 Clash 对 {nodes.Count} 个节点发起本轮检测，不读取历史延迟…";
                SetRunningUi(false);
                SetNodeTestingUi(true);
                ActivityProgressBar.IsIndeterminate = true;

                var clashDelays = await _mihomoClient!.RefreshAndMeasureGroupDelaysAsync(
                    group.Name,
                    nodes.Select(node => node.Name).ToArray(),
                    new Uri("https://www.gstatic.com/generate_204"),
                    timeoutMilliseconds: 3000,
                    expectedStatus: "200-299",
                    _nodeTestCancellation.Token);
                NodeTestStatusText.Text =
                    "Clash 本轮主动检测已完成，正在按最新结果筛选节点…";
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
                var routeGroup = ResolveNodeTestRouteGroup(
                    group.Name,
                    nodes.Select(node => node.Name));
                await BeginNodeTestRoutingAsync(
                    routeGroup,
                    _nodeTestCancellation.Token);
                NodeTestStatusText.Text =
                    $"第一关完成：{nodes.Count} → {clashReachableCount}；"
                    + (routeGroup.Equals("GLOBAL", StringComparison.OrdinalIgnoreCase)
                        ? "测速期间将临时使用全局模式并在结束后恢复；"
                        : "")
                    + $"正在临时使用 {bootstrap.Name} 读取画廊图片地址…";
                await _mihomoClient.SelectProxyAsync(
                    routeGroup, bootstrap.Name, _nodeTestCancellation.Token);
                await Task.Delay(350, _nodeTestCancellation.Token);
                _gallerySamples = await GallerySampleService.GetSampleUrlsAsync(
                    engine,
                    url,
                    options,
                    maximumItems: 1,
                    cancellationToken: _nodeTestCancellation.Token);
                _gallerySampleRefresh = cancellationToken =>
                    GallerySampleService.GetSampleUrlsAsync(
                        engine,
                        url,
                        options,
                        maximumItems: 1,
                        cancellationToken: cancellationToken);

                realTestCandidates = NodeScreeningLogic.SelectForRealTest(
                    NodeResults,
                    testAllReachable,
                    NodeScreeningLogic.DefaultSmartLimit);
                foreach (var result in NodeResults.Where(result => result.ClashDelayMs > 0))
                {
                    result.EligibleForRealTest = realTestCandidates.Contains(result);
                    if (result.EligibleForRealTest)
                    {
                        result.Rating = "待实测";
                        result.Status = "Clash 可用，等待真实下载";
                        result.Details =
                            $"Clash 延迟 {result.ClashDelayMs} ms。"
                            + "接下来会真实读取图片数据；首次失败时会重新分配图片服务器再试一次。";
                    }
                    else
                    {
                        result.Rating = "候选保留";
                        result.Status = "智能模式暂不实测";
                        result.Details =
                            "Clash 联网检测通过；为缩短时间，本轮不进行真实下载。"
                            + "可选择“检测全部可用节点”重新测试。";
                    }
                }
                SortNodeResults();
                if (realTestCandidates.Count == 0)
                    throw new InvalidOperationException(
                        "Clash 快速检测没有留下可用于真实测速的节点。建议稍后重试。");

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
                if (!string.IsNullOrWhiteSpace(_nodeTestGroup) && !await RestoreNodeAfterTestAsync())
                    screeningCompleted = false;
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
                $"{nodes.Count}→{clashReachableCount}→{realTestCandidates.Count}";
            NodeTestStatusText.Text =
                $"快速筛选完成（总数→联网→实测：{summary}），开始真实图片下载测速…";
            await RunNodeTestStageAsync(
                group.Name,
                realTestCandidates,
                gallerySamples.Take(1).ToList(),
                512L * 1024,
                5,
                $"真实快测 {summary}",
                _gallerySampleRefresh);
        }
        catch (Exception ex) { NodeTestStatusText.Text = FriendlyError(ex.Message); }
        finally
        {
            _isPreparingNodeTest = false;
            SetRunningUi(false);
        }
    }

    private async void PreciseNodeTestButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsOperationBusy || _gallerySamples is null
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
            "精确复测",
            _gallerySampleRefresh);
    }

    private async Task RunNodeTestStageAsync(
        string groupName,
        IReadOnlyList<NodeProbeResult> results,
        IReadOnlyList<Uri> samples,
        long bytesPerSample,
        int timeoutSeconds,
        string stageName,
        Func<CancellationToken, Task<IReadOnlyList<Uri>>>? refreshSamples = null)
    {
        if (_mihomoClient is null || _clashState is null || samples.Count == 0) return;
        var routeGroup = ResolveNodeTestRouteGroup(
            groupName,
            results.Select(result => result.Name));

        _nodeTestCancellation = new CancellationTokenSource();
        _isNodeTesting = true;
        SetRunningUi(false);
        SetNodeTestingUi(true);
        try
        {
            await BeginNodeTestRoutingAsync(
                routeGroup,
                _nodeTestCancellation.Token);
        }
        catch (Exception ex)
        {
            var restored = true;
            if (!string.IsNullOrWhiteSpace(_nodeTestGroup))
                restored = await RestoreNodeAfterTestAsync();
            _nodeTestCancellation.Dispose();
            _nodeTestCancellation = null;
            _isNodeTesting = false;
            SetRunningUi(false);
            SetNodeTestingUi(false);
            if (restored) NodeTestStatusText.Text =
                $"无法建立独立的节点测速通道：{FriendlyError(ex.Message)}";
            return;
        }
        _isNodeTesting = true;
        SetRunningUi(false);
        SetNodeTestingUi(true);
        ActivityProgressBar.IsIndeterminate = false;
        ActivityProgressBar.Value = 0;
        var completedNormally = true;
        var probeOptions = CreateJob(_nodeTestGalleryUrl ?? "").Options;
        var service = new GalleryNodeTestService(
            _mihomoClient,
            NormalizeProxy(ProxyTextBox.Text),
            _nodeTestGalleryUrl,
            GetSelectedTag(LoginModeComboBox, "manual") == "manual"
                ? GetManualCookie()
                : null,
            _engineManager.FindEngine(),
            probeOptions.ForceIpv4);

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
                        routeGroup,
                        result.Name,
                        samples,
                        bytesPerSample,
                        timeoutSeconds,
                        progress,
                        _nodeTestCancellation.Token,
                        refreshSamples);
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
            var restored = await RestoreNodeAfterTestAsync();
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
            if (restored) NodeTestStatusText.Text = completedNormally
                ? best is null
                    ? $"{stageName}完成，但没有稳定通过的节点。可以换一个策略组再试。"
                    : $"{stageName}完成，已恢复原节点。当前推荐：{best.Name}；选中后点击“使用选中节点”。"
                : "测试已停止，并已恢复测试前使用的节点。";
            SetRunningUi(false);
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
        NodeResultsGrid.Items.SortDescriptions.Clear();
        foreach (var column in NodeResultsGrid.Columns)
            column.SortDirection = null;

        var sorted = NodeProbeRanking.Order(NodeResults);
        for (var target = 0; target < sorted.Count; target++)
        {
            var current = NodeResults.IndexOf(sorted[target]);
            if (current != target) NodeResults.Move(current, target);
            sorted[target].Rank =
                sorted[target].SuccessfulSamples > 0
                    ? sorted.Take(target + 1).Count(item => item.SuccessfulSamples > 0)
                    : 0;
        }

        foreach (var result in NodeResults.Where(result =>
                     result.SampleCount > 0
                     && result.SuccessfulSamples == result.SampleCount))
            result.Rating = "稳定";
        var best = NodeResults.FirstOrDefault(result =>
            result.SampleCount > 0
            && result.SuccessfulSamples == result.SampleCount);
        if (best is not null) best.Rating = "推荐";
        NodeResultsGrid.Items.Refresh();
    }

    private void SetNodeTestingUi(bool testing)
    {
        QuickNodeTestButton.IsEnabled = !testing
                                        && !IsOperationBusy
                                        && NodeGroupComboBox.SelectedItem is ProxyInfo;
        PreciseNodeTestButton.IsEnabled = !testing && !IsOperationBusy
                                          && NodeResults.Any(result =>
                                              result.SuccessfulSamples > 0);
        StopNodeTestButton.IsEnabled = testing;
        UseTestedNodeButton.IsEnabled = !testing && !IsOperationBusy
                                        && NodeResultsGrid.SelectedItem is NodeProbeResult
                                        {
                                            SuccessfulSamples: > 0
                                        };
        NodeGroupComboBox.IsEnabled = !testing && !IsOperationBusy;
        NodeTestModeComboBox.IsEnabled = !testing && !IsOperationBusy;
        OpenNodeTestButton.IsEnabled = !testing && !IsOperationBusy;
    }

    private void StopNodeTestButton_Click(object sender, RoutedEventArgs e)
    {
        StopNodeTestButton.IsEnabled = false;
        NodeTestStatusText.Text = "正在停止测试并恢复原节点…";
        _nodeTestCancellation?.Cancel();
    }

    private async void UseTestedNodeButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsOperationBusy || _mihomoClient is null
            || string.IsNullOrWhiteSpace(_resultGroup)
            || NodeResultsGrid.SelectedItem is not NodeProbeResult
            {
                SuccessfulSamples: > 0
            } result)
            return;
        _isPreparingNodeTest = true;
        SetRunningUi(false);
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
        finally
        {
            _isPreparingNodeTest = false;
            SetRunningUi(false);
        }
    }

    private void NodeResultsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UseTestedNodeButton.IsEnabled =
            !IsOperationBusy
            && NodeResultsGrid.SelectedItem is NodeProbeResult
            {
                SuccessfulSamples: > 0
            };
        if (NodeResultsGrid.SelectedItem is NodeProbeResult result)
            NodeDetailText.Text = result.Details;
        else
            NodeDetailText.Text = "选择一个节点后，这里显示完整测试过程和失败原因。";
    }

    private async Task SaveNodeTestRecoveryAsync()
    {
        if (_mihomoClient is null || string.IsNullOrWhiteSpace(_nodeTestGroup)
            || string.IsNullOrWhiteSpace(_nodeBeforeTest))
            throw new InvalidOperationException("无法确定当前节点，已取消测速以保护原连接。");
        Directory.CreateDirectory(SettingsStore.DataDirectory);
        var recovery = new NodeTestRecoveryState(
            _mihomoClient.Endpoint,
            _nodeTestGroup,
            _nodeBeforeTest,
            _modeBeforeNodeTest ?? "");
        // Do not change routing unless recovery information is safely saved.
        SettingsStore.WriteAllTextAtomic(_nodeRecoveryPath, JsonSerializer.Serialize(recovery));
        await Task.CompletedTask;
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
            if (!string.IsNullOrWhiteSpace(recovery.Mode))
                await _mihomoClient.SetModeAsync(recovery.Mode);
            File.Delete(_nodeRecoveryPath);
            NodeTestStatusText.Text =
                $"已恢复上次意外中断前使用的节点：{recovery.Node}";
        }
        catch
        {
            // 保留恢复记录，下次连接成功后继续尝试。
        }
    }

    private async Task<bool> RestoreNodeAfterTestAsync()
    {
        if (_mihomoClient is null || string.IsNullOrWhiteSpace(_nodeTestGroup)
            || string.IsNullOrWhiteSpace(_nodeBeforeTest))
            return true;
        try
        {
            await _mihomoClient.SelectProxyAsync(
                _nodeTestGroup, _nodeBeforeTest, CancellationToken.None);
            if (!string.IsNullOrWhiteSpace(_modeBeforeNodeTest))
                await _mihomoClient.SetModeAsync(
                    _modeBeforeNodeTest,
                    CancellationToken.None);
            _clashState = await _mihomoClient.GetStateAsync();
            if (File.Exists(_nodeRecoveryPath)) File.Delete(_nodeRecoveryPath);
            return true;
        }
        catch
        {
            NodeTestStatusText.Text =
                "暂时无法恢复原节点，已保存恢复记录；下次打开选线面板时会自动恢复。";
            return false;
        }
        finally
        {
            _nodeTestGroup = null;
            _nodeBeforeTest = null;
            _modeBeforeNodeTest = null;
        }
    }

    private string ResolveNodeTestRouteGroup(
        string selectedGroup,
        IEnumerable<string> nodeNames)
    {
        if (_clashState is not null
            && _clashState.Proxies.TryGetValue("GLOBAL", out var global)
            && global.Type.Equals("Selector", StringComparison.OrdinalIgnoreCase))
        {
            var available = global.All.ToHashSet(StringComparer.Ordinal);
            if (nodeNames.All(available.Contains))
                return global.Name;
        }
        return selectedGroup;
    }

    private async Task BeginNodeTestRoutingAsync(
        string routeGroup,
        CancellationToken cancellationToken)
    {
        if (_mihomoClient is null || _clashState is null
            || !_clashState.Proxies.TryGetValue(routeGroup, out var currentGroup))
            throw new InvalidOperationException("所选策略组已经不存在，请重新连接 Clash。");

        _nodeTestGroup = routeGroup;
        _nodeBeforeTest = currentGroup.Now;
        _modeBeforeNodeTest = _clashState.Mode;
        await SaveNodeTestRecoveryAsync();

        if (routeGroup.Equals("GLOBAL", StringComparison.OrdinalIgnoreCase)
            && !_clashState.Mode.Equals("global", StringComparison.OrdinalIgnoreCase))
            await _mihomoClient.SetModeAsync("global", cancellationToken);
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
