using System.Windows;

namespace EhGalleryDownloader;

public partial class BrowserCookieConsentWindow : Window
{
    public BrowserCookieConsentWindow() => InitializeComponent();

    private void AllowButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
