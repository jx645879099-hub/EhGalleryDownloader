using System.Windows;
using System.Windows.Controls;
using System.Diagnostics;

namespace EhGalleryDownloader;

public partial class BrowserCookieConsentWindow : Window
{
    private readonly string _extensionPath = Path.Combine(AppContext.BaseDirectory, "BrowserExtension");

    public BrowserCookieConsentWindow()
    {
        InitializeComponent();
        ExtensionPathTextBox.Text = _extensionPath;
    }

    private void CopyExtensionAddressButton_Click(object sender, RoutedEventArgs e)
    {
        var browser = (BrowserChoiceComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "chrome";
        var address = browser == "edge" ? "edge://extensions/" : "chrome://extensions/";
        Clipboard.SetText(address);
        GuideStatusText.Text = $"已复制 {address}。请粘贴到浏览器地址栏，按回车打开扩展管理页。";
    }

    private void CopyExtensionPathButton_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(_extensionPath);
        GuideStatusText.Text = "文件夹路径已复制。在“加载已解压的扩展程序”的文件夹选择窗口中粘贴它。";
    }

    private void OpenExtensionFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(_extensionPath))
        {
            GuideStatusText.Text = "程序目录缺少 BrowserExtension 文件夹，请重新解压完整包。";
            return;
        }
        Process.Start(new ProcessStartInfo { FileName = _extensionPath, UseShellExecute = true });
        GuideStatusText.Text = "已打开扩展文件夹。请在浏览器“加载已解压的扩展程序”时选择这个文件夹。";
    }

    private void AllowButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
